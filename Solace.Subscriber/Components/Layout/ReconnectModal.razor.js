// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
        reconnectModal.close();
    } else if (event.detail.state === "failed" || event.detail.state === "rejected") {
        reconnectModal.classList.remove("components-reconnect-show", "components-reconnect-retrying");
        reconnectModal.classList.add("components-reconnect-failed");
        if (!reconnectModal.open) {
            reconnectModal.showModal();
        }
    }
}

async function retry() {
    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                reconnectModal.classList.remove("components-reconnect-show", "components-reconnect-retrying");
                reconnectModal.classList.add("components-reconnect-failed");
            } else {
                reconnectModal.close();
            }
        }
    } catch (err) {
        reconnectModal.classList.remove("components-reconnect-show");
        reconnectModal.classList.add("components-reconnect-failed");
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            reconnectModal.classList.remove("components-reconnect-paused");
            reconnectModal.classList.add("components-reconnect-resume-failed");
        }
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
    }
}
