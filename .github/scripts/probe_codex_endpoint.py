"""Verify the configured Responses endpoint without logging credentials or URLs."""

import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        return None


def main():
    endpoint = os.environ.get("CODEX_LB_BASE_URL", "").strip().rstrip("/")
    api_key = os.environ.get("OPENAI_API_KEY", "").strip()
    parsed = urllib.parse.urlsplit(endpoint)
    if (parsed.scheme != "https" or not parsed.hostname or parsed.username
            or parsed.password or parsed.query or parsed.fragment or not api_key):
        print("Endpoint configuration invalid or required secret missing.")
        return 1

    print(f"::add-mask::{endpoint}")
    print(f"::add-mask::{parsed.hostname}")
    model = os.environ.get("CANARY_MODEL", "").strip() or "gpt-6-astra"
    if model not in {"gpt-6-astra", "gpt-5.6-sol"}:
        print("Canary model must be one of the two approved campaign models.")
        return 1
    print("Selected canary model: " + model)
    payload = {
        "model": model,
        "instructions": "Reply with exactly CANARY_OK and no other text.",
        "input": [{"role": "user", "content": [
            {"type": "input_text", "text": "Check the connection."}]}],
        "store": False,
        "stream": True,
        "reasoning": {"effort": "high"},
    }
    request = urllib.request.Request(
        endpoint + "/responses", data=json.dumps(payload).encode(),
        headers={"Authorization": "Bearer " + api_key,
                 "Content-Type": "application/json", "Accept": "text/event-stream"},
        method="POST")
    text_parts = []
    completed = False
    size = 0
    event_types = set()
    try:
        with urllib.request.build_opener(NoRedirect).open(request, timeout=90) as response:
            print(f"Responses endpoint HTTP status: {response.status}")
            content_type = response.headers.get_content_type()
            print("Streaming content type: " + str(content_type == "text/event-stream"))
            for line in response:
                size += len(line)
                if size > 1024 * 1024:
                    raise ValueError("Response limit exceeded")
                if not line.startswith(b"data: ") or line.strip() == b"data: [DONE]":
                    continue
                event = json.loads(line[6:])
                event_type = event.get("type", "")
                if re.fullmatch(r"[a-z_.]{1,80}", event_type):
                    event_types.add(event_type)
                if event_type in {"error", "response.failed"}:
                    detail = event.get("error") or event.get("response", {}).get("error") or {}
                    code = str(detail.get("code", "unknown"))
                    if re.fullmatch(r"[a-zA-Z0-9_.-]{1,80}", code):
                        print("Endpoint error code: " + code)
                if event.get("type") == "response.output_text.delta":
                    text_parts.append(event.get("delta", ""))
                if event.get("type") == "response.completed":
                    completed = True
                    break
    except urllib.error.HTTPError as error:
        print(f"Responses endpoint HTTP status: {error.code}")
        hints = {401: "Credential rejected.", 403: "Access denied.",
                 404: "Configured base path or model route was not found.",
                 429: "Rate or capacity limit reached."}
        print(hints.get(error.code, "Endpoint rejected the request; response body suppressed."))
        return 1
    except Exception as error:
        print(f"Endpoint probe failed ({type(error).__name__}); connection details suppressed.")
        return 1
    if not completed or "".join(text_parts).strip() != "CANARY_OK":
        print("Completed response: " + str(completed))
        print("Observed event types: " + ", ".join(sorted(event_types)))
        print("Endpoint did not return the required completed canary response.")
        return 1
    print("Endpoint canary passed: authenticated streaming Responses request completed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
