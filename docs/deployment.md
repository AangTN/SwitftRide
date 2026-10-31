# Demo deployment on Lightsail

This deployment uses the existing demo configuration: simulated OTP, seeded accounts,
SQL Server Developer, and the Development environment required by those features.
It is intended for demonstrations. Secrets and database volumes persist on the VPS.

Nginx runs in Docker on public ports 80 and 443. The rider app is at `/`, the driver
app at `/driver/`, and `/api/` is forwarded to Gateway. Gateway trusts only Nginx's
Docker address for forwarded headers; API services trust Gateway. Database and
direct API ports remain bound to the VPS loopback interface.

## Server preparation

Use Ubuntu with Docker Engine, Docker Compose v2, OpenSSL and curl. Give the deploy
user access to Docker. On a 4 GB demo server, create a persistent 4 GB swap file.
Create `/opt/swiftride`, owned by the deploy user (`ubuntu` in this workflow).
Allow inbound TCP 80 and 443, and SSH 22. Keep database ports private.

Copy `compose.yml`, `compose.deploy.yml` and `deploy/` into `/opt/swiftride`.
Run `bash deploy/initialize-demo.sh` once. Existing secrets are preserved.

## HTTPS without a domain

Let’s Encrypt supports public IP certificates using the `shortlived` profile.
Before the first deployment, issue a certificate while port 80 is available:

```bash
cd /opt/swiftride
export PUBLIC_HOST=YOUR_STATIC_PUBLIC_IP
docker run --rm -p 80:80 \
  -v /opt/swiftride/.tls/letsencrypt:/etc/letsencrypt \
  certbot/certbot:latest certonly --standalone \
  --preferred-profile shortlived --ip-address "$PUBLIC_HOST" \
  --cert-name "$PUBLIC_HOST" --non-interactive --agree-tos \
  --register-unsafely-without-email
```

IP certificates expire after roughly six days. Install a systemd timer to run
`bash /opt/swiftride/deploy/renew-tls.sh` twice daily. Renewal uses HTTP webroot
validation and reloads Nginx. Keep port 80 open for certificate validation.
Monitor failures with `journalctl -u swiftride-tls.service`.

```bash
sudo cp deploy/swiftride-tls.service deploy/swiftride-tls.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now swiftride-tls.timer
```

## GitHub Actions

The workflow runs backend tests and Flutter checks, builds images on GitHub runners,
pushes them to GHCR, and deploys only after both build jobs pass. Each release uses
its full commit SHA as the image tag. VPS builds are not required.

Configure repository Actions secrets:

- `DEPLOY_HOST`: static public IP matching the certificate.
- `DEPLOY_SSH_KEY`: dedicated SSH private key authorized for the deploy user.
- `DEPLOY_KNOWN_HOSTS`: verified SSH host key line for the server.

`GITHUB_TOKEN` is provided automatically. Workflow permissions allow publishing
and reading this repository's GHCR packages. The deploy job logs out of GHCR after
deployment. The Lightsail account key is not stored in GitHub.

Push to `main` or run **CI and deploy demo** manually. Concurrent releases are
serialized. Check the Actions run and `docker compose` health before demonstrating.

To redeploy a previously published commit manually, log into GHCR with a token that
can read the packages, then run:

```bash
bash deploy/release.sh FULL_COMMIT_SHA YOUR_STATIC_PUBLIC_IP
docker logout ghcr.io
```

Database migrations run at startup. Reverting application images does not reverse
migrations; back up databases before schema changes. Do not use `down -v` when
updating the stack.

Sources: [Let’s Encrypt IP certificate setup](https://letsencrypt.org/2026/03/11/shorter-certs-certbot/).
