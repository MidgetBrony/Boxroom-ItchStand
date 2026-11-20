# The Itch Stand 1.0.2

## Fixed

- Persists automatic itch.io catalogue attempt times across BOXROOM restarts.
- Waits two hours before retrying a failed automatic catalogue refresh, preventing repeated 429 responses during restart-heavy sessions.
- Continues using the cached catalogue while cooling down; the refresh case and Apply Now remain explicit overrides.
