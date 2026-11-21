// Copies a service's MCP address to the clipboard: the "Copy address" button, and "Add to ChatGPT",
// whose page cannot be prefilled, so the address is ready to paste there (#114). The link itself still
// opens. A button then says what happened: "Copied", or, where the browser refuses the clipboard (some
// in-app browsers do), "Address selected", with the address selected for copying by hand.

// The older copy command, which some embedded browsers allow when the Clipboard API is refused.
function copyWithCommand(text) {
  const area = document.createElement('textarea');
  area.value = text;
  area.setAttribute('readonly', '');
  area.style.position = 'fixed';
  area.style.opacity = '0';
  document.body.appendChild(area);
  area.select();
  try { return document.execCommand('copy'); } catch { return false; } finally { area.remove(); }
}

async function copy(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return copyWithCommand(text);
  }
}

// Selects the address shown beside the buttons, so it can be copied by hand.
function selectAddress(target) {
  const address = target.closest('li')?.querySelector('code');
  if (!address) return;
  const range = document.createRange();
  range.selectNodeContents(address);
  const selection = window.getSelection();
  selection.removeAllRanges();
  selection.addRange(range);
}

function say(target, text) {
  if (target.tagName !== 'BUTTON') return;
  const label = target.dataset.label ?? target.textContent;
  target.dataset.label = label;
  target.textContent = text;
  clearTimeout(target.copyTimer);
  target.copyTimer = setTimeout(() => { target.textContent = label; }, 2000);
}

document.addEventListener('click', async (event) => {
  const target = event.target instanceof Element ? event.target.closest('[data-copy]') : null;
  if (!target) return;
  if (await copy(target.getAttribute('data-copy'))) {
    say(target, 'Copied');
  } else {
    selectAddress(target);
    say(target, 'Address selected');
  }
});
