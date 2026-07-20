/* ============ Foundry RAG Ajanı — panel mantığı ============ */
"use strict";

const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => document.querySelectorAll(sel);

const state = {
  history: [],          // {role, content}
  reportFormat: "docx",
  statusTimer: null,
  docsTimer: null,
  lastStatus: null,
  docs: [],             // yüklü belgeler (mention menüsü + rapor kapsamı için)
  docQuery: "",
  docSort: "date-desc",
  selectedDocIds: new Set(),
  lastVisibleDocIds: [],
};

/* ---------- Yardımcılar ---------- */

function escapeHtml(text) {
  return text
    .replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;").replaceAll("'", "&#39;");
}

/** Mini Markdown → HTML (güvenli: önce escape, sonra biçim) */
function renderMarkdown(raw) {
  const lines = escapeHtml(raw).split("\n");
  let html = "", inList = false, tableBuf = [];

  const flushTable = () => {
    if (tableBuf.length === 0) return;
    const rows = tableBuf.map(l => l.replace(/^\||\|$/g, "").split("|").map(c => c.trim()));
    const isSep = (r) => r.every(c => /^:?-{2,}:?$/.test(c));
    let thead = "", bodyRows = rows;
    if (rows.length > 1 && isSep(rows[1])) {
      thead = `<tr>${rows[0].map(c => `<th>${inline(c)}</th>`).join("")}</tr>`;
      bodyRows = rows.slice(2);
    }
    const tbody = bodyRows.filter(r => !isSep(r))
      .map(r => `<tr>${r.map(c => `<td>${inline(c)}</td>`).join("")}</tr>`).join("");
    html += `<table>${thead}${tbody}</table>`;
    tableBuf = [];
  };

  const inline = (s) => s
    .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
    .replace(/\*(.+?)\*/g, "<em>$1</em>")
    .replace(/`(.+?)`/g, "<code>$1</code>");

  const closeList = () => { if (inList) { html += "</ul>"; inList = false; } };

  for (const line of lines) {
    const t = line.trim();
    if (t.startsWith("|")) { closeList(); tableBuf.push(t); continue; }
    flushTable();

    if (t.length === 0) { closeList(); continue; }

    const heading = t.match(/^(#{1,3})\s+(.*)/);
    if (heading) { closeList(); html += `<h${heading[1].length + 1}>${inline(heading[2])}</h${heading[1].length + 1}>`; continue; }

    const bullet = t.match(/^([-*]|\d+[.)])\s+(.*)/);
    if (bullet) {
      if (!inList) { html += "<ul>"; inList = true; }
      html += `<li>${inline(bullet[2])}</li>`;
      continue;
    }

    closeList();
    html += `<p>${inline(t)}</p>`;
  }
  closeList(); flushTable();
  return html;
}

function toast(message, type = "info", ms = 4200) {
  const el = document.createElement("div");
  el.className = `toast ${type}`;
  el.textContent = message;
  $("#toasts").appendChild(el);
  setTimeout(() => el.remove(), ms);
}

function formatSize(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1048576).toFixed(1)} MB`;
}

function formatDate(iso) {
  return new Date(iso).toLocaleString("tr-TR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

async function api(path, options = {}) {
  const res = await fetch(`/api${path}`, {
    headers: options.body && !(options.body instanceof FormData)
      ? { "Content-Type": "application/json" } : undefined,
    ...options,
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `İstek başarısız (${res.status})`);
  return data;
}

/* ---------- Görünüm geçişi ---------- */

$$(".nav-item").forEach(btn => {
  btn.addEventListener("click", () => {
    $$(".nav-item").forEach(b => b.classList.remove("active"));
    btn.classList.add("active");
    $$(".view").forEach(v => v.classList.remove("active"));
    $(`#view-${btn.dataset.view}`).classList.add("active");
    if (btn.dataset.view === "documents") loadDocuments();
    if (btn.dataset.view === "reports") loadReports();
  });
});

/* ---------- Tema ---------- */

function applyTheme(theme) {
  if (theme === "light") document.documentElement.setAttribute("data-theme", "light");
  else document.documentElement.removeAttribute("data-theme");
  $("#themeIcon").textContent = theme === "light" ? "☀️" : "🌙";
  $("#themeLabel").textContent = theme === "light" ? "Açık tema" : "Koyu tema";
}

applyTheme(localStorage.getItem("theme") === "light" ? "light" : "dark");

$("#themeToggle").addEventListener("click", () => {
  const next = document.documentElement.getAttribute("data-theme") === "light" ? "dark" : "light";
  localStorage.setItem("theme", next);
  applyTheme(next);
});

/* ---------- Durum ---------- */

async function refreshStatus() {
  try {
    const s = await api("/status");
    state.lastStatus = s;

    const dot = $("#pillDot"), pillText = $("#pillText");
    dot.className = "pill-dot";
    if (s.state === "ready") {
      dot.classList.add("ready");
      pillText.textContent = `${s.chatModel.alias} · hazır`;
    } else if (s.state === "error") {
      dot.classList.add("error");
      pillText.textContent = "Model hatası — Durum sekmesi";
    } else {
      dot.classList.add("loading");
      const pct = Math.max(s.chatModel.downloadPercent || 0, 0);
      pillText.textContent = s.state === "downloading"
        ? `Model iniyor %${pct.toFixed(0)}`
        : "Model hazırlanıyor…";
    }

    $("#docBadge").textContent = s.documentCount;
    $("#reportBadge").textContent = s.reportCount;

    // Durum sekmesi
    const stateTr = { starting: "Başlatılıyor", initializing: "Hazırlanıyor", downloading: "Model İndiriliyor", loading: "Model Yükleniyor", ready: "Çalışıyor", error: "Hata" };
    $("#stateValue").textContent = stateTr[s.state] || s.state;
    $("#stateValue").style.color = s.state === "ready" ? "var(--ok)" : s.state === "error" ? "var(--danger)" : "var(--warn)";
    $("#stateMsg").textContent = s.message;
    $("#retryBtn").hidden = s.state !== "error";

    $("#chatModelValue").textContent = s.chatModel.id || s.chatModel.alias;
    $("#chatModelBar").style.width = `${s.state === "ready" ? 100 : (s.chatModel.downloadPercent || 0)}%`;
    $("#embedModelValue").textContent = s.embeddingModel.id || s.embeddingModel.alias;
    $("#embedModelBar").style.width = `${s.state === "ready" ? 100 : (s.embeddingModel.downloadPercent || 0)}%`;

    $("#storeValue").textContent = `${s.documentCount} belge · ${s.chunkCount} parça`;
    $("#storeMsg").textContent = `${s.reportCount} rapor üretildi`;

    $("#techRows").innerHTML = [
      ["Çıkarım endpoint'i", s.inferenceEndpoint],
      ["Sohbet modeli", s.chatModel.id || `${s.chatModel.alias} (yükleniyor)`],
      ["Embedding modeli", s.embeddingModel.id || `${s.embeddingModel.alias} (yükleniyor)`],
      ["Donanım (EP)", s.ep.name || "otomatik seçilecek"],
      ["Veri dizini", s.dataDir],
    ].map(([k, v]) => `<div class="tech-row"><span class="k">${escapeHtml(k)}</span><span class="v">${escapeHtml(String(v ?? "—"))}</span></div>`).join("");

    // Hazır olunca yoklamayı yavaşlat
    clearTimeout(state.statusTimer);
    state.statusTimer = setTimeout(refreshStatus, s.state === "ready" ? 10000 : 2500);
  } catch {
    clearTimeout(state.statusTimer);
    state.statusTimer = setTimeout(refreshStatus, 4000);
  }
}

/* ---------- Sohbet ---------- */

const chatMessages = $("#chatMessages");
const chatText = $("#chatText");

function addMessage(role, contentHtml) {
  $("#chatWelcome")?.remove();
  const wrap = document.createElement("div");
  wrap.className = `msg ${role}`;
  wrap.innerHTML = `
    <div class="msg-avatar">${role === "user" ? "🧑" : "🤖"}</div>
    <div class="msg-bubble">${contentHtml}</div>`;
  chatMessages.appendChild(wrap);
  chatMessages.scrollTop = chatMessages.scrollHeight;
  return wrap.querySelector(".msg-bubble");
}

function renderSources(sources) {
  if (!sources || sources.length === 0) return "";
  const items = sources.map(s =>
    `<div class="source-item"><strong>${escapeHtml(s.fileName)}</strong> · parça ${s.chunkIndex + 1} · benzerlik ${(s.score * 100).toFixed(0)}%<br>${escapeHtml(s.snippet)}</div>`
  ).join("");
  return `
    <div class="msg-sources">
      <button class="sources-toggle" onclick="this.nextElementSibling.classList.toggle('open')">📎 ${sources.length} kaynak parçası</button>
      <div class="sources-list">${items}</div>
    </div>`;
}

function renderReportCard(report) {
  if (!report) return "";
  const icons = { docx: "📝", xlsx: "📊", pdf: "📕" };
  return `
    <div class="report-card">
      <div class="report-card-icon">${icons[report.format] || "📄"}</div>
      <div class="report-card-info">
        <div class="report-card-title">${escapeHtml(report.title)}</div>
        <div class="report-card-sub">${report.format.toUpperCase()} · ${escapeHtml(report.fileName)}</div>
      </div>
      <a class="btn btn-primary btn-sm" href="/api/reports/${report.id}/download">İndir</a>
    </div>`;
}

/** SSE akışını okuyup olay olay işler (fetch + ReadableStream; EventSource POST desteklemez). */
async function sendChat(message) {
  addMessage("user", renderMarkdown(message));
  state.history.push({ role: "user", content: message });
  $("#chatSend").disabled = true;

  const bubble = addMessage("assistant",
    '<div class="msg-typing"><span></span><span></span><span></span></div>');

  let answer = "";
  let sources = null;
  let report = null;
  let errorMsg = null;

  const renderLive = () => {
    bubble.innerHTML = renderMarkdown(answer) + '<span class="stream-cursor">▍</span>';
    chatMessages.scrollTop = chatMessages.scrollHeight;
  };

  const handleEvent = (e) => {
    switch (e.type) {
      case "status":
        // Cevap akmaya başladıysa ara durumları gösterme
        if (!answer) {
          bubble.innerHTML = `<p class="stream-status"><span class="spinner"></span> ${escapeHtml(e.message)}</p>`;
          chatMessages.scrollTop = chatMessages.scrollHeight;
        }
        break;
      case "delta":
        answer += e.text;
        renderLive();
        break;
      case "sources":
        sources = e.sources;
        break;
      case "report":
        report = e.report;
        loadReports();
        break;
      case "error":
        errorMsg = e.message;
        break;
    }
  };

  try {
    const res = await fetch("/api/chat/stream", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ message, history: state.history.slice(0, -1) }),
    });
    if (!res.ok || !res.body) {
      const data = await res.json().catch(() => ({}));
      throw new Error(data.error || `Sunucu hatası (${res.status})`);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const events = buffer.split("\n\n");
      buffer = events.pop();
      for (const raw of events) {
        const dataLine = raw.split("\n").find(l => l.startsWith("data: "));
        if (!dataLine) continue;
        try { handleEvent(JSON.parse(dataLine.slice(6))); } catch { /* bozuk satırı atla */ }
      }
    }

    if (errorMsg) {
      bubble.innerHTML = (answer ? renderMarkdown(answer) : "") + `<p>⚠️ ${escapeHtml(errorMsg)}</p>`;
    } else {
      bubble.innerHTML = renderMarkdown(answer)
        + renderReportCard(report)
        + renderSources(sources || []);
    }
    if (answer) state.history.push({ role: "assistant", content: answer });
    chatMessages.scrollTop = chatMessages.scrollHeight;
  } catch (err) {
    bubble.innerHTML = `<p>⚠️ ${escapeHtml(err.message)}</p>`;
  } finally {
    $("#chatSend").disabled = false;
    chatText.focus();
  }
}

$("#chatForm").addEventListener("submit", (e) => {
  e.preventDefault();
  const message = chatText.value.trim();
  if (!message) return;
  chatText.value = "";
  chatText.style.height = "auto";
  sendChat(message);
});

chatText.addEventListener("keydown", (e) => {
  if (e.key === "Escape") { hideMentionMenu(); return; }
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    hideMentionMenu();
    $("#chatForm").requestSubmit();
  }
});
chatText.addEventListener("input", () => {
  chatText.style.height = "auto";
  chatText.style.height = Math.min(chatText.scrollHeight, 140) + "px";
  updateMentionMenu();
});

/* ---------- @Bahsetme menüsü ---------- */

const mentionMenu = $("#mentionMenu");

function hideMentionMenu() { mentionMenu.hidden = true; }

/** İmleçten geriye doğru aktif "@token"ı bulur (boşluk görünce durur). */
function activeMentionToken() {
  const upToCaret = chatText.value.slice(0, chatText.selectionStart);
  const match = upToCaret.match(/@([^\s@]*)$/);
  return match ? { text: match[1], start: upToCaret.length - match[0].length } : null;
}

function updateMentionMenu() {
  const token = activeMentionToken();
  if (!token) { hideMentionMenu(); return; }

  const query = token.text.toLocaleLowerCase("tr");
  const matches = state.docs.filter(d =>
    d.status === "ready" && d.fileName.toLocaleLowerCase("tr").includes(query));
  if (matches.length === 0) { hideMentionMenu(); return; }

  mentionMenu.innerHTML = matches
    .map(d => `<button type="button" class="mention-item" data-name="${escapeHtml(d.fileName)}">📄 ${escapeHtml(d.fileName)}</button>`)
    .join("");
  mentionMenu.hidden = false;

  mentionMenu.querySelectorAll(".mention-item").forEach(btn => {
    btn.addEventListener("click", () => {
      const t = activeMentionToken();
      if (!t) { hideMentionMenu(); return; }
      const before = chatText.value.slice(0, t.start);
      const after = chatText.value.slice(chatText.selectionStart);
      chatText.value = `${before}@${btn.dataset.name} ${after}`;
      hideMentionMenu();
      chatText.focus();
      const caret = (before + "@" + btn.dataset.name + " ").length;
      chatText.setSelectionRange(caret, caret);
    });
  });
}

document.addEventListener("click", (e) => {
  if (!mentionMenu.contains(e.target) && e.target !== chatText) hideMentionMenu();
});

$$(".chip").forEach(chip => {
  chip.addEventListener("click", () => {
    chatText.value = chip.dataset.example;
    chatText.focus();
  });
});

/* ---------- Belgeler ---------- */

const fileIcons = { ".docx": "📝", ".pdf": "📕", ".xlsx": "📊", ".csv": "📈", ".txt": "📃", ".md": "📃" };
const statusTr = { ready: "hazır", processing: "işleniyor", queued: "kuyrukta", error: "hata" };

function refreshScopeSelect() {
  const select = $("#reportScope");
  const current = select.value;
  select.innerHTML = '<option value="">📚 Tüm belgeler</option>' +
    state.docs
      .filter(d => d.status === "ready")
      .map(d => `<option value="${d.id}">📄 ${escapeHtml(d.fileName)}</option>`)
      .join("");
  if ([...select.options].some(o => o.value === current)) select.value = current;
}

function filterDocs(docs, query) {
  const q = query.trim().toLocaleLowerCase("tr");
  if (!q) return docs;
  return docs.filter(d => d.fileName.toLocaleLowerCase("tr").includes(q));
}

function sortDocs(docs, sortKey) {
  const arr = [...docs];
  const byName = (a, b) => a.fileName.localeCompare(b.fileName, "tr");
  switch (sortKey) {
    case "date-asc": return arr.sort((a, b) => new Date(a.uploadedAtUtc) - new Date(b.uploadedAtUtc));
    case "name-asc": return arr.sort(byName);
    case "name-desc": return arr.sort((a, b) => byName(b, a));
    case "size-desc": return arr.sort((a, b) => b.sizeBytes - a.sizeBytes);
    case "size-asc": return arr.sort((a, b) => a.sizeBytes - b.sizeBytes);
    case "status": return arr.sort((a, b) => (a.status || "").localeCompare(b.status || "", "tr"));
    case "date-desc":
    default: return arr.sort((a, b) => new Date(b.uploadedAtUtc) - new Date(a.uploadedAtUtc));
  }
}

function updateBulkBar(visibleIds) {
  const count = state.selectedDocIds.size;
  $("#bulkBar").hidden = count === 0;
  $("#bulkCount").textContent = `${count} belge seçildi`;

  const selectAll = $("#docSelectAll");
  const visibleSelected = visibleIds.filter(id => state.selectedDocIds.has(id));
  selectAll.checked = visibleIds.length > 0 && visibleSelected.length === visibleIds.length;
  selectAll.indeterminate = visibleSelected.length > 0 && visibleSelected.length < visibleIds.length;
}

function renderDocsTable() {
  const filtered = sortDocs(filterDocs(state.docs, state.docQuery), state.docSort);
  const body = $("#docsBody");
  const empty = $("#docsEmpty");

  // Artık listede olmayan belgelerin seçimini temizle (silinmiş vb.)
  const liveIds = new Set(state.docs.map(d => d.id));
  for (const id of state.selectedDocIds) if (!liveIds.has(id)) state.selectedDocIds.delete(id);

  if (state.docs.length === 0) {
    empty.textContent = "Henüz belge yok — yukarıdan yükleyebilirsin.";
    empty.style.display = "block";
  } else if (filtered.length === 0) {
    empty.textContent = "Aramanla eşleşen belge yok.";
    empty.style.display = "block";
  } else {
    empty.style.display = "none";
  }

  body.innerHTML = filtered.map(d => `
      <tr data-id="${d.id}">
        <td class="checkbox-col"><input type="checkbox" class="doc-checkbox" data-id="${d.id}" ${state.selectedDocIds.has(d.id) ? "checked" : ""}></td>
        <td><div class="file-cell"><span class="file-ico">${fileIcons[d.extension] || "📄"}</span>${escapeHtml(d.fileName)}</div></td>
        <td>${formatSize(d.sizeBytes)}</td>
        <td><span class="status-chip ${d.status}">${d.status === "processing" ? '<span class="spinner"></span>' : ""}${statusTr[d.status] || d.status}${d.error ? ` — ${escapeHtml(d.error)}` : ""}</span></td>
        <td>${d.chunkCount || "—"}</td>
        <td>${formatDate(d.uploadedAtUtc)}</td>
        <td>
          <div class="row-actions">
            <button class="btn btn-sm" onclick="summarizeDoc(${d.id}, this)" ${d.status !== "ready" ? "disabled" : ""}>Özetle</button>
            <button class="btn btn-sm btn-danger" onclick="deleteDoc(${d.id}, '${escapeHtml(d.fileName).replace(/'/g, "\\'")}')">Sil</button>
          </div>
        </td>
      </tr>
      ${d.summary ? `<tr class="summary-row"><td colspan="7"><strong>Özet:</strong>\n${escapeHtml(d.summary)}</td></tr>` : ""}
    `).join("");

  body.querySelectorAll(".doc-checkbox").forEach(cb => {
    cb.addEventListener("change", () => {
      const id = Number(cb.dataset.id);
      if (cb.checked) state.selectedDocIds.add(id); else state.selectedDocIds.delete(id);
      updateBulkBar(filtered.map(d => d.id));
    });
  });

  state.lastVisibleDocIds = filtered.map(d => d.id);
  updateBulkBar(state.lastVisibleDocIds);
}

async function loadDocuments() {
  try {
    const docs = await api("/documents");
    state.docs = docs;
    refreshScopeSelect();
    renderDocsTable();

    // İşlenen belge varsa yenilemeye devam et
    clearTimeout(state.docsTimer);
    if (docs.some(d => d.status === "processing" || d.status === "queued")) {
      state.docsTimer = setTimeout(loadDocuments, 3000);
    }
  } catch (err) {
    toast(`Belgeler yüklenemedi: ${err.message}`, "error");
  }
}

$("#docSearch").addEventListener("input", (e) => {
  state.docQuery = e.target.value;
  renderDocsTable();
});
$("#docSort").addEventListener("change", (e) => {
  state.docSort = e.target.value;
  renderDocsTable();
});

$("#docSelectAll").addEventListener("change", (e) => {
  const visible = state.lastVisibleDocIds || [];
  if (e.target.checked) visible.forEach(id => state.selectedDocIds.add(id));
  else visible.forEach(id => state.selectedDocIds.delete(id));
  renderDocsTable();
});

$("#bulkDelete").addEventListener("click", async () => {
  const ids = [...state.selectedDocIds];
  if (ids.length === 0) return;
  if (!confirm(`${ids.length} belge ve tüm parçaları silinsin mi?`)) return;

  const btn = $("#bulkDelete");
  btn.disabled = true;
  const results = await Promise.allSettled(ids.map(id => api(`/documents/${id}`, { method: "DELETE" })));
  const failed = results.filter(r => r.status === "rejected").length;
  const ok = results.length - failed;
  if (ok > 0) toast(`${ok} belge silindi.`, "success");
  if (failed > 0) toast(`${failed} belge silinemedi.`, "error");

  state.selectedDocIds.clear();
  btn.disabled = false;
  loadDocuments();
  refreshStatus();
});

$("#bulkSummarize").addEventListener("click", async () => {
  const ids = [...state.selectedDocIds].filter(id => {
    const doc = state.docs.find(d => d.id === id);
    return doc && doc.status === "ready";
  });
  if (ids.length === 0) { toast("Seçili belgeler arasında özetlenebilir (hazır) belge yok.", "error"); return; }

  const btn = $("#bulkSummarize");
  btn.disabled = true;
  const results = await Promise.allSettled(ids.map(id => api(`/documents/${id}/summarize`, { method: "POST" })));
  const failed = results.filter(r => r.status === "rejected").length;
  const ok = results.length - failed;
  if (ok > 0) toast(`${ok} belge özetlendi.`, "success");
  if (failed > 0) toast(`${failed} belge özetlenemedi.`, "error");

  btn.disabled = false;
  loadDocuments();
});

async function uploadFiles(files) {
  if (!files || files.length === 0) return;
  const form = new FormData();
  for (const f of files) form.append("files", f);

  toast(`${files.length} dosya yükleniyor…`);
  try {
    const results = await api("/documents/upload", { method: "POST", body: form });
    const failed = results.filter(r => !r.ok);
    if (failed.length > 0)
      failed.forEach(f => toast(`${f.fileName}: ${f.error}`, "error", 6000));
    const okCount = results.length - failed.length;
    if (okCount > 0)
      toast(`${okCount} dosya kuyruğa alındı — model hazırsa hemen işlenecek.`, "success");
    loadDocuments();
  } catch (err) {
    toast(`Yükleme hatası: ${err.message}`, "error");
  }
}

window.summarizeDoc = async (id, btn) => {
  btn.disabled = true;
  btn.innerHTML = '<span class="spinner"></span>';
  try {
    await api(`/documents/${id}/summarize`, { method: "POST" });
    toast("Özet hazır.", "success");
    loadDocuments();
  } catch (err) {
    toast(`Özetleme hatası: ${err.message}`, "error", 6000);
  } finally {
    btn.disabled = false;
    btn.textContent = "Özetle";
  }
};

window.deleteDoc = async (id, name) => {
  if (!confirm(`"${name}" belgesi ve tüm parçaları silinsin mi?`)) return;
  try {
    await api(`/documents/${id}`, { method: "DELETE" });
    toast("Belge silindi.", "success");
    loadDocuments();
    refreshStatus();
  } catch (err) {
    toast(`Silme hatası: ${err.message}`, "error");
  }
};

const dropzone = $("#dropzone");
dropzone.addEventListener("click", () => $("#fileInput").click());
$("#browseBtn").addEventListener("click", (e) => { e.stopPropagation(); $("#fileInput").click(); });
$("#fileInput").addEventListener("change", (e) => { uploadFiles(e.target.files); e.target.value = ""; });
["dragover", "dragenter"].forEach(ev => dropzone.addEventListener(ev, (e) => { e.preventDefault(); dropzone.classList.add("dragover"); }));
["dragleave", "drop"].forEach(ev => dropzone.addEventListener(ev, (e) => { e.preventDefault(); dropzone.classList.remove("dragover"); }));
dropzone.addEventListener("drop", (e) => uploadFiles(e.dataTransfer.files));

/* ---------- Raporlar ---------- */

$$("#formatSeg .seg-item").forEach(btn => {
  btn.addEventListener("click", () => {
    $$("#formatSeg .seg-item").forEach(b => b.classList.remove("active"));
    btn.classList.add("active");
    state.reportFormat = btn.dataset.format;
  });
});

async function loadReports() {
  try {
    const reports = await api("/reports");
    $("#reportsEmpty").style.display = reports.length === 0 ? "block" : "none";
    $("#reportsBody").innerHTML = reports.map(r => `
      <tr>
        <td style="font-weight:600">${escapeHtml(r.title)}</td>
        <td><span class="format-badge ${r.format}">${r.format.toUpperCase()}</span></td>
        <td>${formatDate(r.createdAtUtc)}</td>
        <td>
          <div class="row-actions">
            <a class="btn btn-sm" href="/api/reports/${r.id}/download">İndir</a>
            <button class="btn btn-sm btn-danger" onclick="deleteReport(${r.id})">Sil</button>
          </div>
        </td>
      </tr>`).join("");
  } catch (err) {
    toast(`Raporlar yüklenemedi: ${err.message}`, "error");
  }
}

window.deleteReport = async (id) => {
  if (!confirm("Rapor silinsin mi?")) return;
  try {
    await api(`/reports/${id}`, { method: "DELETE" });
    toast("Rapor silindi.", "success");
    loadReports();
    refreshStatus();
  } catch (err) {
    toast(`Silme hatası: ${err.message}`, "error");
  }
};

$("#reportCreate").addEventListener("click", async () => {
  const instruction = $("#reportInstruction").value.trim();
  if (!instruction) { toast("Önce rapor talimatı yaz.", "error"); return; }

  $("#reportCreate").disabled = true;
  $("#reportProgress").hidden = false;
  try {
    const scopeValue = $("#reportScope").value;
    await api("/reports", {
      method: "POST",
      body: JSON.stringify({
        instruction,
        format: state.reportFormat,
        documentId: scopeValue ? Number(scopeValue) : null,
      }),
    });
    toast("Rapor oluşturuldu! 🎉", "success");
    $("#reportInstruction").value = "";
    loadReports();
    refreshStatus();
  } catch (err) {
    toast(`Rapor hatası: ${err.message}`, "error", 7000);
  } finally {
    $("#reportCreate").disabled = false;
    $("#reportProgress").hidden = true;
  }
});

$("#retryBtn").addEventListener("click", async () => {
  try {
    await api("/foundry/retry", { method: "POST" });
    toast("Yeniden deneme başlatıldı…", "success");
    setTimeout(refreshStatus, 800);
  } catch (err) {
    toast(err.message, "error");
  }
});

/* ---------- Başlangıç ---------- */
refreshStatus();
loadDocuments();
loadReports();
