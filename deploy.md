# Deploy (VM)

```bash
cd ~/project-manager-bot
git fetch origin
git checkout main
git pull --ff-only origin main
systemctl restart projectmanagerbot
```
