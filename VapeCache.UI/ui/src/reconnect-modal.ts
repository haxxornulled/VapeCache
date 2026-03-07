type ReconnectState = "show" | "hide" | "failed" | "rejected" | string;

interface ReconnectStateDetail {
  state?: ReconnectState;
}

interface BlazorReconnectApi {
  reconnect(): Promise<boolean>;
  resumeCircuit(): Promise<boolean>;
}

declare global {
  interface Window {
    Blazor?: BlazorReconnectApi;
  }
}

const reconnectModal = document.getElementById("components-reconnect-modal");
if (!(reconnectModal instanceof HTMLDialogElement)) {
  throw new Error("Missing reconnect modal element: #components-reconnect-modal");
}

const retryButton = document.getElementById("components-reconnect-button");
if (!(retryButton instanceof HTMLButtonElement)) {
  throw new Error("Missing reconnect button element: #components-reconnect-button");
}

const resumeButton = document.getElementById("components-resume-button");
if (!(resumeButton instanceof HTMLButtonElement)) {
  throw new Error("Missing resume button element: #components-resume-button");
}

function getReconnectApi(): BlazorReconnectApi {
  const api = window.Blazor;
  if (!api) {
    throw new Error("Blazor reconnect API is unavailable.");
  }

  return api;
}

function getReconnectState(event: Event): ReconnectState {
  if (!(event instanceof CustomEvent)) {
    return "";
  }

  const detail = event.detail as ReconnectStateDetail | undefined;
  return detail?.state ?? "";
}

function handleReconnectStateChanged(event: Event): void {
  const state = getReconnectState(event);
  if (state === "show") {
    reconnectModal.showModal();
    return;
  }

  if (state === "hide") {
    reconnectModal.close();
    return;
  }

  if (state === "failed") {
    document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    return;
  }

  if (state === "rejected") {
    location.reload();
  }
}

async function retry(): Promise<void> {
  document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

  try {
    const api = getReconnectApi();
    const reconnectSuccessful = await api.reconnect();
    if (reconnectSuccessful) {
      reconnectModal.close();
      return;
    }

    const resumeSuccessful = await api.resumeCircuit();
    if (!resumeSuccessful) {
      location.reload();
      return;
    }

    reconnectModal.close();
  } catch {
    document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
  }
}

async function resume(): Promise<void> {
  try {
    const api = getReconnectApi();
    const successful = await api.resumeCircuit();
    if (!successful) {
      location.reload();
    }
  } catch {
    reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
  }
}

async function retryWhenDocumentBecomesVisible(): Promise<void> {
  if (document.visibilityState === "visible") {
    await retry();
  }
}

reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);
retryButton.addEventListener("click", () => {
  void retry();
});
resumeButton.addEventListener("click", () => {
  void resume();
});
