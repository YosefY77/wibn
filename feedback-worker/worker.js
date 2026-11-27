// Wibn's suggestion box relay (Cloudflare Worker).
//
// The app posts here; this posts into the developer's Discord channel. The Discord
// webhook lives only here, as a secret (wrangler secret put DISCORD_WEBHOOK), never
// in the app, so nobody can pull it out of Wibn.exe and flood or abuse the channel.
//
// What it checks, on the server where it can't be skipped:
//   * one message per 60 s per sender (Cloudflare's rate limiter; the IP is only used
//     as the limiter's key: it's never stored or sent on),
//   * kind, version and language from fixed lists; text 3..1500 characters,
//   * the text goes in a code block with no mentions, so links aren't clickable and
//     @everyone pings nobody.

const MAX_TEXT = 1500;
const recent = new Map(); // sender → when their last message came (memory only, never stored)
const KINDS = { suggestion: "💡 Suggestion", complaint: "⚠️ Complaint", bug: "🐞 Bug" };

function reply(status, error) {
  return new Response(JSON.stringify(error ? { ok: false, error } : { ok: true }), {
    status,
    headers: { "content-type": "application/json" },
  });
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method !== "POST" || url.pathname !== "/send") return reply(404, "not_found");
    if (!env.DISCORD_WEBHOOK) return reply(503, "not_ready");

    // Anything bigger than a message can be isn't one.
    const length = Number(request.headers.get("content-length") || 0);
    if (length > 8192) return reply(413, "too_long");

    const ip = request.headers.get("cf-connecting-ip") || "unknown";
    // Cloudflare's limiter counts a moment late, so a quick second message can slip by it:
    // this worker's own memory catches those (it's per server, so the limiter stays too).
    const now = Date.now();
    if (recent.size > 5000) recent.clear();
    if (now - (recent.get(ip) || 0) < 60_000) return reply(429, "slow_down");
    recent.set(ip, now);
    if (env.LIMITER) {
      const { success } = await env.LIMITER.limit({ key: ip });
      if (!success) return reply(429, "slow_down");
    }

    let body;
    try { body = await request.json(); } catch { return reply(400, "bad_request"); }

    const kind = KINDS[body?.kind] ? body.kind : null;
    const text = typeof body?.text === "string" ? body.text.trim() : "";
    const version = typeof body?.version === "string" && /^\d{1,3}(\.\d{1,4}){0,3}$/.test(body.version) ? body.version : "?";
    const lang = body?.lang === "en" ? "English" : "العربية";
    if (!kind) return reply(400, "bad_kind");
    if (text.length < 3) return reply(400, "too_short");
    if (text.length > MAX_TEXT) return reply(400, "too_long");

    // In a code block: no markdown, no clickable links. Backticks can't close it early.
    const safe = text.replace(/`/g, "ˋ");
    const content = `**${KINDS[kind]}** · Wibn ${version} · ${lang}\n\`\`\`\n${safe}\n\`\`\``;

    const hook = env.DISCORD_WEBHOOK.replace(/^﻿/, "").trim();
    let res;
    try {
      res = await fetch(hook, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        username: "Wibn",
        content,
        allowed_mentions: { parse: [] },
        flags: 4, // no link previews
      }),
      });
    } catch {
      // Never let the error (it would print the webhook) reach the logs.
      return reply(502, "discord_unreachable");
    }
    if (!res.ok) return reply(502, "discord_" + res.status);
    return reply(200);
  },
};
