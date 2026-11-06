const MENU_ID = "litedm-download-link";

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: MENU_ID,
      title: "Download with LiteDM",
      contexts: ["link"]
    });
  });
});

chrome.contextMenus.onClicked.addListener((info) => {
  if (info.menuItemId !== MENU_ID || !info.linkUrl) return;
  sendToLiteDM({ action: "addDownload", url: info.linkUrl }, info.linkUrl);
});

chrome.runtime.onMessage.addListener((message) => {
  if (message?.type === "retry" && typeof message.url === "string") {
    sendToLiteDM({ action: "addDownload", url: message.url }, message.url);
  } else if (message?.type === "open") {
    sendToLiteDM({ action: "show", url: "" });
  }
});

function sendToLiteDM(message, retryUrl = "") {
  if (message.action === "addDownload" && !isSupportedUrl(message.url)) {
    saveStatus({ ok: false, message: "Only valid HTTP and HTTPS links can be sent to LiteDM.", url: "" });
    return;
  }

  chrome.runtime.sendNativeMessage("com.litedm.bridge", message, (response) => {
    const runtimeError = chrome.runtime.lastError;
    if (runtimeError) {
      saveStatus({ ok: false, message: runtimeError.message || "Could not connect to LiteDM.", url: retryUrl });
      return;
    }

    if (!response || !response.accepted) {
      saveStatus({ ok: false, message: response?.error || "LiteDM did not accept the request.", url: retryUrl });
      return;
    }

    saveStatus({
      ok: true,
      message: message.action === "show"
        ? "LiteDM opened."
        : "Sent to LiteDM for review. Confirm the download in the app.",
      url: ""
    });
  });
}

function isSupportedUrl(value) {
  try {
    const parsed = new URL(value);
    return (parsed.protocol === "http:" || parsed.protocol === "https:")
      && parsed.hostname.length > 0
      && !parsed.username
      && !parsed.password;
  } catch {
    return false;
  }
}

function saveStatus(status) {
  const updatedAt = new Date().toISOString();
  chrome.storage.local.set({ lastHandoff: { ...status, updatedAt } }, () => {
    chrome.action.setBadgeBackgroundColor({ color: status.ok ? "#2563A6" : "#8A3B35" });
    chrome.action.setBadgeText({ text: status.ok ? "OK" : "!" });
    chrome.action.setTitle({ title: `LiteDM: ${status.message}` });
  });
}
