"""Transport failures may be probed later without weakening evidence requirements."""

from state import Rejected


class InfrastructureError(Rejected):
    """A command/transport failed before a trustworthy semantic result was available."""
