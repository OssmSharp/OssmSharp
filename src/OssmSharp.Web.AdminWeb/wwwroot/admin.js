const attachmentLengthKey = "ossm-admin-attachment-length-cm";

/**
 * Reads the illustrative attachment length, in centimetres.
 * @param {number} fallback value used when nothing valid is stored.
 * @returns {number} the stored length in centimetres, or the fallback.
 */
export function readAttachmentLength(fallback) {
  try {
    const stored = Number.parseFloat(localStorage.getItem(attachmentLengthKey) ?? "");
    return Number.isFinite(stored) ? stored : fallback;
  } catch {
    return fallback;
  }
}

/**
 * Stores the illustrative attachment length, in centimetres, for this browser.
 * @throws {DOMException} Browser storage is unavailable or full.
 */
export function writeAttachmentLength(value) {
  localStorage.setItem(attachmentLengthKey, String(value));
}

export async function copy(text) {  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
