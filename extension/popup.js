const statusEl = document.getElementById("status");
const detailEl = document.getElementById("detail");
const retryButton = document.getElementById("retry");

chrome.storage.local.get("lastHandoff", ({ lastHandoff }) => {
  if (lastHandoff) {
    render(lastHandoff);
  } else {
    statusEl.textContent = "Ready to connect";
  }
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes.lastHandoff) render(changes.lastHandoff.newValue);
});

retryButton.addEventListener("click", () => {
  chrome.storage.local.get("lastHandoff", ({ lastHandoff }) => {
    if (!lastHandoff?.url) return;
    retryButton.disabled = true;
    statusEl.textContent = "Retrying…";
    chrome.runtime.sendMessage({ type: "retry", url: lastHandoff.url });
    window.setTimeout(() => { retryButton.disabled = false; }, 1200);
  });
});

document.getElementById("open").addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "open" });
});

function render(result) {
  statusEl.textContent = result.ok ? "Ready" : "Connection needs attention";
  detailEl.textContent = result.message;
  retryButton.hidden = !(result.url && !result.ok);
}
