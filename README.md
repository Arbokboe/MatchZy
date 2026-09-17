## Custom Fork Modifications

This build contains dedicated improvements, security fixes, and platform-specific adaptations for the **DeloPlay** tournament backend:

* **Secure Webhook Authentication:** Remote event logger loads custom authorization headers directly from a local server configuration file (`csgo/cfg/MatchZy/secret.cfg`), preventing token leakage through public match configuration endpoints.
* **Thread-Safe Config Resolution:** Configuration file paths (`Server.GameDirectory`) and auth secrets are resolved strictly on the main server thread at plugin startup, eliminating cross-thread native invocation crashes (`Native was invoked on a non-main thread`).
* **Reliable HTTP Header Injection:** Webhooks utilize isolated per-request header injection (`TryAddWithoutValidation`), ensuring strict delivery of authorization tokens to Spring Boot backend services without header reuse conflicts.
* **UUID & Identifier Normalization:** Added full support for string-formatted UUIDs for `matchId`, `team1.id`, and `team2.id` across both JSON config parsing and outgoing event payloads.
* **Flexible Webhook Mapping:** Implemented dynamic support for both `camelCase` and `snake_case` serialization (`remoteLogUrl` / `remote_log_url`), ensuring zero friction with Jackson/Java backends.
* **Robust Match Loader Safety:** Fixed potential `NullReferenceException` cases and added strict type validation when loading incomplete or dynamically generated match JSON configurations.
* **Custom Platform Branding:** Fully rebranded plugin identity, including chat prefixes, server console logging, and custom administration tags.
