# Deploy (VM)

```bash
cd ~/project-manager-bot
git fetch origin
git checkout main
git pull --ff-only origin main
systemctl restart projectmanagerbot
```

View logs:

```bash
journalctl -u projectmanagerbot -n 200 -f
```
