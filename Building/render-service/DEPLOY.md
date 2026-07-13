# Deploying the render service on a VPS

End-to-end: a fresh Ubuntu box → renders appearing on your base cards.

Nothing here touches your managed game host except one config file.

---

## 0. What you need

* **A VPS** running Ubuntu 22.04 or 24.04.
  * 2 GB RAM minimum (4 GB comfortable — headless Chrome is the hungry part).
  * ~10 GB free disk (the model library is the bulk of it).
  * No GPU required. Any $5/month box works.
* **Its public IP** — call it `VPS_IP` below.
* File access to your game server (FTP/panel), which you already have.

---

## 1. Install Node and Chrome's system libraries

SSH into the VPS as root:

```bash
apt update
curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
apt install -y nodejs git

# Libraries headless Chrome needs (it renders in software, no GPU driver needed).
# The audio lib was renamed in Ubuntu 24.04, so install whichever this box has.
apt install -y libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 libxkbcommon0 \
  libxcomposite1 libxdamage1 libxfixes3 libxrandr2 libgbm1 libpango-1.0-0 \
  libcairo2 fonts-liberation
apt install -y libasound2t64 || apt install -y libasound2
```

Check: `node -v` should print `v20.x`.

---

## 2. Put the service on the box

Upload the whole `render-service/` folder (scp, rsync, or just `git clone` your repo):

```bash
mkdir -p /opt
# from your PC:
#   scp -r "render-service" root@VPS_IP:/opt/base-render
cd /opt/base-render
```

---

## 3. Download the Rust model library

This is the ~9,000 `.glb` models Facepunch publishes. Clone it and move the assets in:

```bash
cd /opt/base-render
git clone --depth 1 https://github.com/Facepunch/RustRelay.Assets /tmp/rr
mv /tmp/rr/assets ./assets
rm -rf /tmp/rr
```

Sanity check — this file must exist:

```bash
ls "assets/prefabs/Building Core/foundation/foundation.stone.glb"
```

---

## 4. Install dependencies

```bash
cd /opt/base-render
npm install
```

This also downloads the Chromium build Puppeteer drives. Takes a few minutes.

---

## 5. First run (foreground, to see it work)

Pick a secret for the API key — any random string.

```bash
cd /opt/base-render
PUBLIC_URL="http://31.220.92.183:8080" API_KEY="your-secret-here" npm start
```

You should see:

```
Indexed 9100 models from /opt/base-render/assets
Base render service on :8080
Public URL: http://VPS_IP:8080  (put this in the plugin's "Render service URL")
```

From your own PC, open `http://VPS_IP:8080/health` in a browser. It should return
`{"ok":true,"models":9100}`. If it doesn't, the firewall is blocking it — see step 7.

Stop it with Ctrl+C for now.

---

## 6. Run it permanently (systemd)

Edit `base-render.service` in this folder — set `PUBLIC_URL` and `API_KEY` — then:

```bash
cp /opt/base-render/base-render.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now base-render
systemctl status base-render      # should say "active (running)"
journalctl -u base-render -f      # live logs
```

It now starts on boot and restarts if it crashes.

---

## 7. Open the port

```bash
ufw allow 8080/tcp     # if ufw is enabled
```

Some providers (Oracle, AWS, Azure) also have a firewall in their web console — open
TCP 8080 there too. Confirm from your PC that `http://VPS_IP:8080/health` responds.

---

## 8. Point the game server at it

On your **game host**, edit `oxide/config/RandomGridSpawn.json`:

```json
{
  "Render service URL (blank = disabled)": "http://VPS_IP:8080/",
  "Render service API key": "your-secret-here",
  "Render published bases": true,
  "Render quicksaves and autosaves": false
}
```

Keep the trailing `/`. The API key must match exactly what you used in step 5/6.

Then in the server console:

```
o.reload RandomGridSpawn
```

---

## 9. Test it

1. Build something, hit **+ PUBLISH**, leave the image field empty, publish.
2. Watch the VPS logs (`journalctl -u base-render -f`) — you should see
   `[CODE] rendered 42 pieces (188 KB)`.
3. Back in game, a toast says **"3D render ready"** and the card shows the render.

To backfill bases you saved earlier, run in the server console:

```
gridspawn.render L3HW3G3V
```

(one code at a time — use the code shown on each card).

---

## Troubleshooting

**Nothing happens, no logs on the VPS.**
The game server can't reach the VPS. Test from the game server's console/FTP box if you
can, or just confirm `http://VPS_IP:8080/health` loads for you. Usually the provider
firewall (step 7).

**Server console warns "Render service failed (HTTP 401)".**
The API key in the plugin config doesn't match the one the service is running with.

**Warns "HTTP 422 / no models resolved".**
The asset folder isn't where the service expects. Re-check step 3 — the path
`assets/prefabs/Building Core/...` must exist under `/opt/base-render`.

**Renders are blank or the service dies mid-render.**
Out of memory — Chrome needs headroom. Give the box 4 GB, or add swap:
```bash
fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
```

**Some pieces missing from the render.**
Normal for exotic prefabs. The service logs which prefabs it couldn't match and skips
them rather than failing. Send me the list and I'll extend the mapping.

---

## Optional: a proper domain and HTTPS

Not required — plain HTTP over IP works fine, since only your game server calls it and
only clients fetch the images. If you want it tidy later, point a subdomain at the VPS
and put Caddy in front (`caddy reverse-proxy --from renders.yourdomain.com --to :8080`),
then set `PUBLIC_URL="https://renders.yourdomain.com"` and update the plugin config.
