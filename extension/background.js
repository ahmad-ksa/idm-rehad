// Rehad browser integration: takes over downloads, detects video/audio streams on pages,
// and sends them to the Rehad app through the native messaging host "com.rehad.host".
"use strict";

const api = typeof browser !== "undefined" ? browser : chrome;
const HOST = "com.rehad.host";
const isFirefox = typeof browser !== "undefined" && navigator.userAgent.includes("Firefox");

const MEDIA_TYPE = /^(video\/|audio\/|application\/(x-mpegurl|vnd\.apple\.mpegurl|dash\+xml|f4m\+xml))/i;
const MEDIA_EXT = /\.(m3u8|mpd|mp4|m4v|webm|flv|mov|mkv|3gp|mp3|m4a|aac|ogg|opus|wav)(\?|#|$)/i;
const SEGMENT = /\.(ts|m4s|m4f|aac)(\?|$)|[/_-](seg|segment|chunk|frag)[-_]?\d+|\/range\/\d|[?&](range|bytestart)=/i;
const SITE = /(^|\.)(youtube\.com|youtu\.be|tiktok\.com|instagram\.com|facebook\.com|fb\.watch|twitter\.com|x\.com|vimeo\.com|dailymotion\.com|twitch\.tv|reddit\.com|soundcloud\.com|ok\.ru|vk\.com|bilibili\.com|threads\.net|pinterest\.com|snapchat\.com|linkedin\.com)$/i;

const state = { enabled: true, appOk: null, minSize: 300 * 1024, videoButton: true, capture: true };
const media = new Map(); // tabId -> [{url, mime, size, kind, name}]
const session = api.storage.session;

function saveMedia(tabId) {
  if (session) session.set({ ["m" + tabId]: media.get(tabId) || [] }).catch(() => { });
}

async function mediaOf(tabId) {
  if (!media.has(tabId) && session) {
    try { const v = await session.get("m" + tabId); if (v["m" + tabId]) media.set(tabId, v["m" + tabId]); } catch (e) { }
  }
  return media.get(tabId) || [];
}

api.storage.local.get({ enabled: true }).then(v => { state.enabled = v.enabled; badge(); });

function send(msg) {
  return new Promise(resolve => {
    try {
      api.runtime.sendNativeMessage(HOST, msg, reply => {
        if (api.runtime.lastError || !reply) { state.appOk = false; resolve(null); }
        else { state.appOk = true; resolve(reply); }
      });
    } catch (e) { state.appOk = false; resolve(null); }
  });
}

async function ping() {
  const r = await send({ type: "ping" });
  if (r && r.minVideoSize) state.minSize = r.minVideoSize;
  if (r && typeof r.videoButton === "boolean") state.videoButton = r.videoButton;
  if (r && typeof r.capture === "boolean") state.capture = r.capture;
  return !!r;
}
ping();

function badge(tabId) {
  if (!state.enabled) { api.action.setBadgeText({ text: "OFF" }); api.action.setBadgeBackgroundColor({ color: "#888" }); return; }
  api.action.setBadgeText({ text: "" });
  if (tabId != null) {
    const n = (media.get(tabId) || []).length;
    api.action.setBadgeText({ tabId, text: n ? String(n) : "" });
    api.action.setBadgeBackgroundColor({ tabId, color: "#1c8f9e" });
  }
}

api.action.onClicked.addListener(async () => {
  state.enabled = !state.enabled;
  await api.storage.local.set({ enabled: state.enabled });
  badge();
});

async function cookiesFor(url) {
  try {
    const list = await api.cookies.getAll({ url });
    return list.map(c => `${c.name}=${c.value}`).join("; ");
  } catch (e) { return ""; }
}

async function payload(type, url, extra = {}) {
  return Object.assign({
    type, url,
    cookies: await cookiesFor(url),
    userAgent: navigator.userAgent,
  }, extra);
}

// ---------------------------------------------------------------- take over browser downloads

const skipOnce = new Set();

api.downloads.onCreated.addListener(async item => {
  if (!state.enabled || !state.capture) return;
  const url = item.finalUrl || item.url;
  if (!/^(https?|ftp):/i.test(url) || skipOnce.has(url)) { skipOnce.delete(url); return; }
  if (state.appOk === false && !(await ping())) return;

  const msg = await payload("download", url, {
    referer: item.referrer || "",
    fileName: (item.filename || "").split(/[\\/]/).pop(),
    mime: item.mime || "",
    size: item.totalBytes > 0 ? item.totalBytes : (item.fileSize > 0 ? item.fileSize : -1),
    capture: true,
  });
  // pause immediately so the browser doesn't keep downloading while we ask the app
  try { await api.downloads.pause(item.id); } catch (e) { }
  const reply = await send(msg);
  if (reply && reply.taken) {
    try { await api.downloads.cancel(item.id); } catch (e) { }
    try { await api.downloads.erase({ id: item.id }); } catch (e) { }
  } else {
    try { await api.downloads.resume(item.id); } catch (e) { }
  }
});

// ---------------------------------------------------------------- detect media requests

api.webRequest.onHeadersReceived.addListener(details => {
  if (details.tabId < 0 || !state.enabled) return;
  const h = {};
  for (const x of details.responseHeaders || []) h[x.name.toLowerCase()] = x.value || "";
  const mime = (h["content-type"] || "").split(";")[0].trim().toLowerCase();
  const url = details.url;
  let host = "";
  try { host = new URL(url).hostname; } catch (e) { return; }
  if (host.endsWith("googlevideo.com")) return; // YouTube: handled as a site page
  const isList = /mpegurl|dash\+xml/.test(mime) || /\.(m3u8|mpd)(\?|$)/i.test(url);
  if (!isList && !(MEDIA_TYPE.test(mime) || (mime === "application/octet-stream" && MEDIA_EXT.test(url)))) return;
  if (!isList && SEGMENT.test(url)) return;
  let size = -1;
  const range = h["content-range"];
  if (range && range.includes("/")) size = parseInt(range.split("/").pop(), 10);
  else if (h["content-length"]) size = parseInt(h["content-length"], 10);
  if (!isList && size > 0 && size < state.minSize) return;

  const list = media.get(details.tabId) || [];
  if (!media.has(details.tabId)) media.set(details.tabId, list);
  const base = url.split("?")[0];
  if (list.some(m => m.url.split("?")[0] === base)) return;
  const kind = isList ? (/mpd|dash/.test(url + mime) ? "dash" : "hls") : (mime.startsWith("audio/") ? "audio" : "video");
  list.unshift({ url, mime, size, kind, name: decodeURIComponent(base.split("/").pop() || "video") });
  media.set(details.tabId, list.slice(0, 30));
  saveMedia(details.tabId);
  badge(details.tabId);
  api.tabs.sendMessage(details.tabId, { type: "rehad-media", count: list.length }).catch(() => { });
}, { urls: ["<all_urls>"], types: ["media", "xmlhttprequest", "object", "other"] }, ["responseHeaders"]);

api.webNavigation.onCommitted.addListener(d => {
  if (d.frameId === 0) { media.delete(d.tabId); saveMedia(d.tabId); badge(d.tabId); }
});
api.tabs.onRemoved.addListener(id => media.delete(id));

// ---------------------------------------------------------------- messages from the page button

api.runtime.onMessage.addListener((msg, sender, reply) => {
  const tab = sender.tab;
  if (!tab) return;
  if (msg.type === "rehad-state") {
    let site = false;
    try { site = SITE.test(new URL(tab.url).hostname); } catch (e) { }
    mediaOf(tab.id).then(list => reply({ enabled: state.enabled && state.videoButton, site, media: list, label: api.i18n.getMessage("dlThis") }));
    return true;
  }
  if (msg.type === "rehad-download") {
    (async () => {
      const m = msg.item;
      let r;
      if (!m) r = await send(await payload("page", tab.url, { title: tab.title, pageUrl: tab.url }));
      else r = await send(await payload(m.kind === "hls" || m.kind === "dash" ? "stream" : "download", m.url,
        { referer: tab.url, pageUrl: tab.url, title: tab.title, mime: m.mime, size: m.size, kind: m.kind }));
      reply({ ok: !!r });
    })();
    return true;
  }
  if (msg.type === "rehad-links") {
    (async () => {
      const r = await send({ type: "links", urls: msg.urls, referer: tab.url, cookies: await cookiesFor(tab.url) });
      reply({ ok: !!r });
    })();
    return true;
  }
});

// ---------------------------------------------------------------- context menus

function menus() {
  api.contextMenus.removeAll(() => {
    api.contextMenus.create({ id: "rehad-link", title: api.i18n.getMessage("dlLink"), contexts: ["link", "image", "video", "audio"] });
    api.contextMenus.create({ id: "rehad-video", title: api.i18n.getMessage("dlVideo"), contexts: ["page", "video"] });
    api.contextMenus.create({ id: "rehad-all", title: api.i18n.getMessage("dlAll"), contexts: ["page", "selection"] });
  });
}
api.runtime.onInstalled.addListener(menus);
api.runtime.onStartup && api.runtime.onStartup.addListener(menus);

api.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === "rehad-link") {
    const url = info.linkUrl || info.srcUrl;
    if (url) send(await payload("download", url, { referer: tab ? tab.url : "" }));
  } else if (info.menuItemId === "rehad-video") {
    const list = await mediaOf(tab.id);
    let site = false;
    try { site = SITE.test(new URL(tab.url).hostname); } catch (e) { }
    if (site || !list.length) send(await payload("page", tab.url, { title: tab.title, pageUrl: tab.url }));
    else {
      const m = list[0];
      send(await payload(m.kind === "hls" || m.kind === "dash" ? "stream" : "download", m.url, { referer: tab.url, pageUrl: tab.url, title: tab.title }));
    }
  } else if (info.menuItemId === "rehad-all") {
    api.tabs.sendMessage(tab.id, { type: "rehad-collect-links", selection: info.selectionText ? true : false }).catch(() => { });
  }
});
