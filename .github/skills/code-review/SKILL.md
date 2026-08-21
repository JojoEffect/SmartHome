---
name: code-review
description: Repository-specific guidance for Copilot pull request review in SmartHome.
---

# SmartHome code review skill

Use this checklist when reviewing pull requests in this repository:

1. Verify Homie payload/topic compatibility and retained message behavior are preserved.
2. Check startup, reconnection, and availability state transitions for regressions.
3. Validate configuration-driven behavior stays backward compatible unless explicitly documented.
4. Confirm logging remains actionable and avoids leaking credentials or secrets.
5. Ensure new async flows handle cancellation, timeout, and error propagation explicitly.
6. Prefer focused findings on correctness, protocol behavior, safety, and runtime reliability.
