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

function addTyping() {
  $("#chatWelcome")?.remove();
  const wrap = document.createElement("div");
  wrap.className = "msg assistant";
  wrap.id = "typingMsg";
  wrap.innerHTML = `
    <div class="msg-avatar">🤖</div>
    <div class="msg-bubble"><div class="msg-typing"><span></span><span></span><span></span></div></div>`;
  chatMessages.appendChild(wrap);
  chatMessages.scrollTop = chatMessages.scrollHeight;
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

async function sendChat(message) {
  addMessage("user", renderMarkdown(message));
  state.history.push({ role: "user", content: message });
  addTyping();
  $("#chatSend").disabled = true;

  try {
    const res = await api("/chat", {
      method: "POST",
      body: JSON.stringify({ message, history: state.history.slice(0, -1) }),
    });
    $("#typingMsg")?.remove();
    addMessage("assistant",
      renderMarkdown(res.answer) + renderReportCard(res.report) + renderSources(res.sources));
    state.history.push({ role: "assistant", content: res.answer });
    if (res.action === "report") loadReports();
  } catch (err) {
    $("#typingMsg")?.remove();
    addMessage("assistant", `<p>⚠️ ${escapeHtml(err.message)}</p>`);
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
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    $("#chatForm").requestSubmit();
  }
});
chatText.addEventListener("input", () => {
  chatText.style.height = "auto";
  chatText.style.height = Math.min(chatText.scrollHeight, 140) + "px";
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

async function loadDocuments() {
  try {
    const docs = await api("/documents");
    const body = $("#docsBody");
    $("#docsEmpty").style.display = docs.length === 0 ? "block" : "none";
    body.innerHTML = docs.map(d => `
      <tr data-id="${d.id}">
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
      ${d.summary ? `<tr class="summary-row"><td colspan="6"><strong>Özet:</strong>\n${escapeHtml(d.summary)}</td></tr>` : ""}
    `).join("");

    // İşlenen belge varsa yenilemeye devam et
    clearTimeout(state.docsTimer);
    if (docs.some(d => d.status === "processing" || d.status === "queued")) {
      state.docsTimer = setTimeout(loadDocuments, 3000);
    }
  } catch (err) {
    toast(`Belgeler yüklenemedi: ${err.message}`, "error");
  }
}

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
    await api("/reports", {
      method: "POST",
      body: JSON.stringify({ instruction, format: state.reportFormat }),
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

/* ---------- Başlangıç ---------- */
refreshStatus();
loadDocuments();
loadReports();
