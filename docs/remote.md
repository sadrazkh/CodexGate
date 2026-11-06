# Codex on the server

**English** · [فارسی](remote.fa.md)

With server sessions, a teammate opens **Server** in the panel, starts a session and gets a full
terminal in the browser, running either Codex or a plain shell on the server. Closing the browser or
putting the laptop to sleep does not stop the session. Opening it again from a laptop or a phone
shows the same screen. Codex inside a session uses the gateway like any other client, so the
teammate's plan limits apply.

## How it is kept apart

- Every teammate who uses server sessions gets a system account of their own, `cg-<id>`, with a
  private home (`/home/cg-<id>`, mode 0700). Their projects live in `~/work/<folder>`.
- Sessions run in tmux as that account. One teammate's Codex cannot read another teammate's files
  or the gateway's data folder (database, cookie keys), which belongs to the gateway's own user
  with mode 0700. The gateway's environment, including upstream keys, is not passed to sessions.
- Root is needed only to create accounts and switch to them. That job belongs to one small script,
  [`deploy/codexgate-host`](../deploy/codexgate-host). The gateway may run it through `sudo` and
  nothing else as root. The script checks every argument against a strict pattern and passes
  nothing to a shell.
- The browser terminal connects with a one-time ticket that expires after 60 seconds. Getting a
  ticket needs the panel cookie and the CSRF header.
- Each server account has its own gateway key, named “Server sessions”. Revoking it stops server
  sessions until the next start, which makes a new one.

## Run it with Docker (simplest)

```bash
docker build -t codexgate-remote --target remote .
docker run -d --name codexgate --restart unless-stopped \
  -p 127.0.0.1:8080:8080 \
  -v codexgate-data:/data -v codexgate-homes:/home \
  -e Gate__Upstreams__0__BaseUrl=https://api.openai.com/v1 \
  -e Gate__Upstreams__0__ApiKey=sk-... \
  codexgate-remote
```

The image contains tmux, Git, Node.js, Codex and the helper, and turns server sessions on. Keep
`/home` on a volume, because that is where teammates' projects live. When the container restarts,
running sessions end and their files stay.

Do not run it with `--security-opt no-new-privileges` or as a read-only filesystem: the helper
needs `sudo`, `useradd` and `runuser`. Per-user memory caps are not applied in a container. Give
the whole container a limit with `--memory` instead.

## Run it on a Linux server

You need tmux 3.2 or newer, `sudo`, Codex (`npm install -g @openai/codex`) and Git.

```bash
sudo useradd --system --create-home --home-dir /var/lib/codexgate codexgate
sudo deploy/install-remote.sh codexgate       # installs the helper and one sudoers line
```

Run CodexGate as `codexgate` with:

```bash
Gate__Remote__Enabled=true
Gate__Remote__GatewayUrl=http://127.0.0.1:8080/v1   # how Codex in a session reaches this gateway
```

With systemd, a plan’s **memory cap** is enforced per user: each account’s sessions run in a scope
under `codexgate.slice` with `MemoryMax`.

## Turn it on for a plan

**Plans → Edit → Sessions on the server**: allow it, choose how many sessions a user may run at
once, and optionally a memory cap. The **Server** page appears for users on that plan.

## Good to know

- “Terminal” sessions are for `git clone`, installing tools for a project, or anything by hand. To
  push to a private repository, a teammate can make an SSH key inside their own account.
- Stopping a session ends what runs in it. Files in `~/work` stay. Deleting a user in the panel stops
  their sessions and leaves the account and its files on the server.
- If the panel says the server could not prepare a session, look at the gateway’s log. `sudo: a
  password is required` means the sudoers line is missing. `no such file` means the helper is not
  installed.
