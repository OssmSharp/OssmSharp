/**
 * Copies an invitation to the browser clipboard.
 * @param {string} text the invitation to copy.
 * @returns {Promise<boolean>} whether the clipboard accepted the invitation.
 */
export async function copy(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
