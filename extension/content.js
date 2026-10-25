// Floating "Download this video" button over the biggest visible video on the page,
// and link collection for "Download all links with Rehad".
"use strict";
(() => {
  if (window.__rehadLoaded) return;
  window.__rehadLoaded = true;
  const api = typeof browser !== "undefined" ? browser : chrome;

  let host, root, button, menu, target = null, info = null, hiddenFor = new WeakSet();

  function build() {
    host = document.createElement("rehad-panel");
    host.style.cssText = "all:initial;position:fixed;z-index:2147483647;left:0;top:0;width:0;height:0;";
    root = host.attachShadow({ mode: "closed" });
    root.innerHTML = `
      <style>
        .wrap{position:fixed;display:none;font:13px/1.2 "Segoe UI",Tahoma,Arial,sans-serif;direction:ltr}
        .btn{display:flex;align-items:center;gap:7px;padding:7px 12px 7px 9px;border-radius:8px;cursor:pointer;
             color:#fff;background:linear-gradient(180deg,#22a6a0,#1d6fc9);box-shadow:0 3px 10px rgba(0,0,0,.35);
             user-select:none;white-space:nowrap;opacity:.92;transition:opacity .15s}
        .btn:hover{opacity:1}
        .ico{width:16px;height:16px}
        .x{margin-left:6px;opacity:.7;font-weight:bold;padding:0 2px}
        .x:hover{opacity:1}
        .menu{display:none;margin-top:4px;background:#fff;color:#222;border-radius:8px;min-width:260px;max-width:420px;
              box-shadow:0 6px 18px rgba(0,0,0,.35);overflow:hidden}
        .item{padding:8px 12px;cursor:pointer;border-bottom:1px solid #eee;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .item:hover{background:#e9f6f5}
        .tag{display:inline-block;min-width:38px;font-size:11px;color:#fff;background:#1d6fc9;border-radius:4px;padding:1px 4px;margin-right:6px;text-align:center}
      </style>
      <div class="wrap">
        <div class="btn"><svg class="ico" viewBox="0 0 24 24"><path fill="#fff" d="M10.5 3h3v8h4l-5.5 6-5.5-6h4zM5 19h14v2.4H5z"/></svg><span class="lbl"></span><span class="x" title="Hide">×</span></div>
        <div class="menu"></div>
      </div>`;
    button = root.querySelector(".btn");
    menu = root.querySelector(".menu");
    button.addEventListener("click", onClick);
    root.querySelector(".x").addEventListener("click", e => {
      e.stopPropagation();
      if (target) hiddenFor.add(target);
      hide();
    });
    document.documentElement.appendChild(host);
  }

  function hide() { if (root) root.querySelector(".wrap").style.display = "none"; menu && (menu.style.display = "none"); }

  function sizeText(n) {
    if (!(n > 0)) return "";
    const u = ["B", "KB", "MB", "GB"]; let i = 0;
    while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
    return n.toFixed(i ? 1 : 0) + " " + u[i];
  }

  function onClick(e) {
    e.stopPropagation();
    if (!info) return;
    const list = info.media || [];
    if (info.site || list.length <= 1) {
      api.runtime.sendMessage({ type: "rehad-download", item: info.site ? null : list[0] || null });
      flash();
      return;
    }
    menu.innerHTML = "";
    for (const m of list) {
      const d = document.createElement("div");
      d.className = "item";
      d.innerHTML = `<span class="tag">${m.kind.toUpperCase()}</span>`;
      d.appendChild(document.createTextNode(`${m.name} ${sizeText(m.size)}`));
      d.title = m.url;
      d.addEventListener("click", ev => { ev.stopPropagation(); menu.style.display = "none"; api.runtime.sendMessage({ type: "rehad-download", item: m }); flash(); });
      menu.appendChild(d);
    }
    menu.style.display = menu.style.display === "block" ? "none" : "block";
  }

  function flash() {
    button.style.opacity = "0.5";
    setTimeout(() => button && (button.style.opacity = ""), 400);
  }

  // biggest visible playing area: <video>, or an <iframe>/<embed> player
  function findTarget() {
    let best = null, bestArea = 0;
    for (const el of document.querySelectorAll("video, embed, object, iframe")) {
      if (hiddenFor.has(el)) continue;
      const r = el.getBoundingClientRect();
      if (r.width < 200 || r.height < 110) continue;
      if (r.bottom < 0 || r.right < 0 || r.top > innerHeight || r.left > innerWidth) continue;
      const st = getComputedStyle(el);
      if (st.visibility === "hidden" || st.display === "none" || st.opacity === "0") continue;
      if (el.localName === "iframe" && !/player|embed|video/i.test(el.src || "")) continue;
      const area = r.width * r.height;
      if (area > bestArea && r.width < r.height * 4) { best = el; bestArea = area; }
    }
    return best;
  }

  function place() {
    if (!info || !info.enabled || (!info.site && !(info.media || []).length)) { hide(); return; }
    const t = findTarget();
    target = t;
    if (!t) { hide(); return; }
    if (!host) build();
    root.querySelector(".lbl").textContent = info.label || "Download this video";
    const r = t.getBoundingClientRect();
    const wrap = root.querySelector(".wrap");
    wrap.style.display = "block";
    const w = wrap.offsetWidth || 190;
    wrap.style.left = Math.max(4, Math.min(innerWidth - w - 8, r.right - w - 10)) + "px";
    wrap.style.top = Math.max(4, r.top + 10) + "px";
  }

  function refresh() {
    try {
      api.runtime.sendMessage({ type: "rehad-state" }, res => {
        if (api.runtime.lastError) return;
        info = res;
        place();
      });
    } catch (e) { }
  }

  api.runtime.onMessage.addListener(msg => {
    if (msg.type === "rehad-media") refresh();
    if (msg.type === "rehad-collect-links") {
      let anchors = [...document.querySelectorAll("a[href]")];
      const sel = getSelection();
      if (msg.selection && sel && sel.rangeCount) anchors = anchors.filter(a => sel.containsNode(a, true));
      const urls = [...new Set(anchors.map(a => a.href).filter(h => /^(https?|ftp):/i.test(h)))];
      api.runtime.sendMessage({ type: "rehad-links", urls });
    }
  });

  let raf = 0;
  const schedule = () => { if (!raf) raf = requestAnimationFrame(() => { raf = 0; place(); }); };
  addEventListener("scroll", schedule, true);
  addEventListener("resize", schedule);
  document.addEventListener("click", () => menu && (menu.style.display = "none"));
  new MutationObserver(() => schedule()).observe(document.documentElement, { childList: true, subtree: true });
  setInterval(refresh, 4000);
  refresh();
})();
