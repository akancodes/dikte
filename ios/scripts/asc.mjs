// The App Store Connect calls the iOS workflow makes with the team's API key (Admin role).
//
//   ASC_KEY_ID=… ASC_ISSUER_ID=… node asc.mjs <command> AuthKey.p8
//
//   bundle-ids        registers the app's and the keyboard's App IDs if they are missing
//   app               fails, saying what to do, while App Store Connect has no app for the bundle id
//   revoke-dev-certs  revokes the development certificates earlier runs made ("Created via API"): every fresh
//                     runner makes one, and the team's cap on them is shared with AkanPanel's builds
//   testers           puts the newest builds in front of the internal group "Us" (made if missing) and the
//                     team members named in TESTERS (first names, comma-separated). Internal testing needs no
//                     review and nothing is published.
import { createPrivateKey, sign } from "node:crypto";
import { readFileSync } from "node:fs";

const APP = "dev.akan.dikte.ios";
const IDS = [
  [APP, "Dikte"],
  [`${APP}.keyboard`, "Dikte Keyboard"],
];

const [command, keyPath] = process.argv.slice(2);
const { ASC_KEY_ID: kid, ASC_ISSUER_ID: iss } = process.env;
const b64 = (b) => Buffer.from(b).toString("base64url");
const now = Math.floor(Date.now() / 1000);
const head = b64(JSON.stringify({ alg: "ES256", kid, typ: "JWT" }));
const claims = b64(JSON.stringify({ iss, iat: now, exp: now + 1200, aud: "appstoreconnect-v1" }));
const signature = sign("sha256", Buffer.from(`${head}.${claims}`), { key: createPrivateKey(readFileSync(keyPath)), dsaEncoding: "ieee-p1363" });
const token = `${head}.${claims}.${b64(signature)}`;

async function api(path, init = {}) {
  const res = await fetch(`https://api.appstoreconnect.apple.com${path}`, {
    ...init,
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
    body: init.body ? JSON.stringify(init.body) : undefined,
  });
  const text = await res.text();
  const json = text ? JSON.parse(text) : {};
  if (!res.ok && !init.soft) throw new Error(`${init.method ?? "GET"} ${path}: ${res.status} ${text.slice(0, 600)}`);
  return { status: res.status, json };
}

async function findApp() {
  const { json } = await api(`/v1/apps?filter[bundleId]=${APP}&limit=5`);
  return json.data.find((a) => a.attributes.bundleId === APP);
}

const commands = {
  async "bundle-ids"() {
    for (const [identifier, name] of IDS) {
      const { json } = await api(`/v1/bundleIds?filter[identifier]=${identifier}&limit=20`);
      if (json.data.some((b) => b.attributes.identifier === identifier)) {
        console.log(`${identifier}: registered`);
        continue;
      }
      await api("/v1/bundleIds", {
        method: "POST",
        body: { data: { type: "bundleIds", attributes: { identifier, name, platform: "IOS" } } },
      });
      console.log(`${identifier}: registered now`);
    }
  },

  async app() {
    const app = await findApp();
    if (app) return console.log(`App Store Connect app: ${app.attributes.name} (${app.id})`);
    console.log(`::error::App Store Connect has no app for ${APP} yet. Apps → + → New App: iOS, any free name, bundle ID "Dikte - ${APP}", SKU dikte-ios. Then run this workflow again.`);
    process.exit(1);
  },

  async "revoke-dev-certs"() {
    const { status, json } = await api("/v1/certificates?filter[certificateType]=DEVELOPMENT,IOS_DEVELOPMENT&limit=200", { soft: true });
    if (status !== 200) return console.log(`::warning::Could not list the team's certificates (${status})`);
    const fromCi = json.data.filter((c) => /created via api/i.test(`${c.attributes.name ?? ""} ${c.attributes.displayName ?? ""}`));
    for (const c of fromCi) {
      const r = await api(`/v1/certificates/${c.id}`, { method: "DELETE", soft: true });
      console.log(`${c.attributes.name}: ${r.status === 204 ? "revoked" : `not revoked (${r.status})`}`);
    }
    console.log(`${fromCi.length} of ${json.data.length} development certificates were made by earlier runs.`);
  },

  async testers() {
    const app = await findApp();
    if (!app) throw new Error(`No App Store Connect app for ${APP}`);
    const groups = (await api(`/v1/apps/${app.id}/betaGroups?limit=50`)).json.data;
    let group = groups.find((g) => g.attributes.isInternalGroup && g.attributes.name === "Us");
    if (!group) {
      group = (
        await api("/v1/betaGroups", {
          method: "POST",
          body: {
            data: {
              type: "betaGroups",
              attributes: { name: "Us", isInternalGroup: true, hasAccessToAllBuilds: true },
              relationships: { app: { data: { type: "apps", id: app.id } } },
            },
          },
        })
      ).json.data;
      console.log(`Made the internal group "Us" (${group.id})`);
    }
    const wanted = (process.env.TESTERS ?? "").split(",").map((s) => s.trim().toLowerCase()).filter(Boolean);
    const users = (await api("/v1/users?limit=100")).json.data;
    const present = new Set(
      (await api(`/v1/betaGroups/${group.id}/betaTesters?limit=200`)).json.data.map((t) => t.attributes.email?.toLowerCase()),
    );
    for (const user of users) {
      const a = user.attributes;
      if (!wanted.includes((a.firstName ?? "").toLowerCase())) continue;
      const email = a.username;
      if (present.has(email?.toLowerCase())) {
        console.log(`${a.firstName}: already in "Us"`);
        continue;
      }
      const r = await api("/v1/betaTesters", {
        method: "POST",
        soft: true,
        body: {
          data: {
            type: "betaTesters",
            attributes: { email, firstName: a.firstName, lastName: a.lastName },
            relationships: { betaGroups: { data: [{ type: "betaGroups", id: group.id }] } },
          },
        },
      });
      console.log(`${a.firstName}: ${r.status < 300 ? "added to \"Us\"" : `not added (${r.status}) ${JSON.stringify(r.json).slice(0, 300)}`}`);
    }
  },
};

if (!commands[command]) {
  console.error(`Unknown command ${command}. One of: ${Object.keys(commands).join(", ")}`);
  process.exit(2);
}
await commands[command]();
