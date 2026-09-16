export async function copy(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}

let logElement;
let logObserver;
let onLogScroll;

export function attachLog(element) {
    detachLog();
    logElement = element;
    let follow = true;
    onLogScroll = () => {
        follow = element.scrollHeight - element.clientHeight - element.scrollTop < 32;
    };
    element.addEventListener("scroll", onLogScroll, { passive: true });
    logObserver = new MutationObserver(() => {
        if (follow) element.scrollTop = element.scrollHeight;
    });
    logObserver.observe(element, { childList: true, subtree: true });
    element.scrollTop = element.scrollHeight;
}

export function detachLog() {
    logObserver?.disconnect();
    if (logElement && onLogScroll) logElement.removeEventListener("scroll", onLogScroll);
    logElement = null;
    logObserver = null;
    onLogScroll = null;
}
