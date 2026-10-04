# Operations (T02 scaffold)

T02 supplies development orchestration only. It does not implement production
service supervision, credential storage, migrations, backups, restore, or
upgrade behavior. Do not treat Aspire AppHost as an always-on macOS service
manager or expose its dashboard publicly.

Use Simulator for credential-free development. Local and Hybrid require
explicit trusted endpoint configuration; use secret references rather than
embedding credentials in URIs or source control. Runtime secret resolution,
backup/restore, native restart policy, and update procedures are owned by T12.
