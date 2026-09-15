"""One-issue repair loop; state and dispatch journals survive process restarts."""

from artifacts import download
from evidence import check_event, event_for, review_event
from feedback import repair_feedback
from patching import create_candidate, publish_candidate
from passing import load_snapshot
from runs import Runs, select_artifact
from state import IDENTITY, ROLES, Rejected, apply_event, new_state, require
from storage import Store


class Campaign:
    def __init__(self, args, control):
        self.args = args
        self.control = control
        self.store = Store(args.repo)
        self.state, self.state_sha = self.store.read(args.campaign)
        creating = self.state is None
        expected = new_state(args.campaign, args.issue, args.integration_base, args.test_source_sha, args.workflow_sha)
        if self.state is None:
            self.state = expected
            self.state["passing_contract"], _ = load_snapshot(args.repository, args.repo, self.state_sha,
                                                              args.integration_base, args.test_source_sha)
        require(isinstance(self.state.get("passing_contract"), dict),
                "Legacy campaign has no passing-contract snapshot; resume it with its pinned runtime")
        for field in IDENTITY:
            require(self.state[field] == expected[field], f"Resume identity changed: {field}")
        journal = self.state.setdefault("orchestration", {"requests": {}, "worker_retries": 0,
                                                          "workflow_ref": args.workflow_ref})
        require(journal["workflow_ref"] == args.workflow_ref, "Resume workflow ref changed")
        self.runs = Runs(args.repo, args.workflow_ref, args.workflow_sha, journal, self.save,
                         max_runs=args.max_runs, timeout_minutes=args.timeout_minutes)
        if creating:
            self.save()

    def save(self):
        self.state_sha = self.store.write(self.state, self.state_sha)

    def refresh(self):
        state, sha = self.store.read(self.args.campaign)
        require(state is not None, "Campaign state disappeared")
        for field in IDENTITY:
            require(state[field] == self.state[field], "Campaign identities changed concurrently")
        require(state.get("passing_contract") == self.state["passing_contract"], "Passing-contract snapshot changed concurrently")
        self.state, self.state_sha = state, sha
        self.runs.journal = self.state["orchestration"]
        require(not self.state["paused"], "Campaign paused; resume explicitly before restarting")

    def record(self, event):
        self.state = apply_event(self.state, event)
        self.runs.journal = self.state["orchestration"]
        self.save()

    def block(self, reason):
        event = event_for(self.state, "block", 0, reason=reason)
        event["event_id"] = f"blocked-{len(self.state['events'])}"
        self.record(event)

    def source_run(self):
        return str(next((event["run_id"] for event in reversed(self.state["history"])
                         if event.get("run_id", 0) > 0), ""))

    def checks(self):
        phase = self.state["phase"]
        retries = self.state["infrastructure_retries"].get(phase, 0)
        key = f"{phase}-{self.state['repair_attempts']}-{retries}"
        inputs = {"issue": str(self.state["issue"]), "base_sha": self.state["base_sha"],
                  "candidate_sha": self.state["candidate_sha"] or "", "level": phase,
                  "accepted_state_sha": self.state["passing_contract"]["state_commit"],
                  "accepted_ledger_sha256": self.state["passing_contract"]["ledger_sha256"]}
        run_id = self.runs.dispatch(key, "bugfix-check.yml", inputs)
        workflow_run = self.runs.wait(run_id)
        self.refresh()
        require(self.state["phase"] == phase, "Campaign advanced concurrently while CI ran")
        if phase == "acceptance" and workflow_run["conclusion"] == "success":
            compatibility = [job for job in self.runs.jobs(run_id) if job["name"] == "compatibility"]
            require(len(compatibility) == 1 and compatibility[0]["conclusion"] == "success",
                    "Acceptance requires the production-build and file-compatibility job")
        event = check_event(self.args.repo, self.state, workflow_run, self.runs.artifacts(run_id), self.control)
        self.record(event)

    def repair(self):
        journal = self.state["orchestration"]
        attempt = self.state["repair_attempts"] + 1
        key = f"fix-{attempt}-{journal['worker_retries']}"
        source_sha = self.state["candidate_sha"] or self.state["base_sha"]
        inputs = {"issue": str(self.state["issue"]), "base_sha": source_sha,
                  "test_source_sha": self.state["test_source_sha"], "source_run": self.source_run(),
                  "feedback": repair_feedback(self.state)}
        run_id = self.runs.dispatch(key, "bugfix-fix.lock.yml", inputs)
        workflow_run = self.runs.wait(run_id)
        self.refresh()
        require(self.state["phase"] == "repairing", "Campaign advanced concurrently while worker ran")
        journal = self.state["orchestration"]
        request = journal["requests"][key]
        try:
            require(workflow_run["conclusion"] == "success", f"Fix worker concluded {workflow_run['conclusion']}")
            if "candidate_sha" not in request:
                artifact = select_artifact(self.runs.artifacts(run_id), f"bugfix-fix-{self.state['issue']}")
                data = download(self.args.repo, artifact)
                candidate, metadata = create_candidate(self.args.repository, self.control, self.args.repo,
                                                        self.state, source_sha, run_id, data)
                request.update(candidate_sha=candidate, worker_metadata=metadata,
                               branch=f"fix/issue-{self.state['issue']}-{self.state['campaign']}-a{attempt}")
                self.save()
            publish_candidate(self.args.repository, self.args.repo, request["branch"], request["candidate_sha"])
        except (Rejected, ValueError, KeyError, OSError) as error:
            journal["worker_retries"] += 1
            request["failure"] = str(error)
            self.save()
            if journal["worker_retries"] > 2:
                self.block(f"Fix worker infrastructure/evidence retries exhausted: {error}")
            return
        event = event_for(self.state, "candidate", run_id, candidate_sha=request["candidate_sha"],
                          branch=request["branch"], worker_metadata=request["worker_metadata"])
        self.record(event)

    def reviews(self):
        candidate = self.state["candidate_sha"]
        pending = []
        for role in ROLES:
            if role in self.state["reviews"]:
                continue
            retries = self.state["infrastructure_retries"].get(f"review:{role}", 0)
            key = f"review-{self.state['repair_attempts']}-{role}-{retries}"
            inputs = {"issue": str(self.state["issue"]), "base_sha": self.state["base_sha"],
                      "candidate_sha": candidate, "test_source_sha": self.state["test_source_sha"],
                      "role": role, "source_run": str(self.state["evidence"]["broad"]["run_id"])}
            pending.append((role, self.runs.dispatch(key, "bugfix-validate.lock.yml", inputs)))
        # Dispatch all roles first so their independent agent runs overlap.
        completed = [(role, self.runs.wait(run_id)) for role, run_id in pending]
        self.refresh()
        require(self.state["candidate_sha"] == candidate, "Candidate changed during review")
        events = [review_event(self.args.repo, self.state, result, self.runs.artifacts(result["id"]), role)
                  for role, result in completed]
        self.state["orchestration"].setdefault("review_reports", {}).setdefault(candidate, []).extend(events)
        self.save()
        for event in events:
            if self.state["phase"] != "reviewing":
                break
            self.record(event)

    def execute(self):
        while True:
            self.refresh()
            phase = self.state["phase"]
            print(f"Campaign {self.state['campaign']}: {phase}; candidates {self.state['repair_attempts']}/3", flush=True)
            if phase in ("ready", "integrated", "blocked"):
                return self.state
            try:
                if phase in ("baseline", "focused", "broad", "acceptance"):
                    self.checks()
                elif phase == "repairing":
                    self.repair()
                elif phase == "reviewing":
                    self.reviews()
                else:
                    raise Rejected(f"Unknown campaign phase: {phase}")
            except (Rejected, ValueError, KeyError, OSError) as error:
                # State-write conflicts and changed remote state must not be overwritten.
                latest, sha = self.store.read(self.args.campaign)
                if sha == self.state_sha and not latest["paused"]:
                    self.block(str(error))
                raise
