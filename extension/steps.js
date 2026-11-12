// The steps that run inside the site's page. This function is injected on its own,
// so everything it needs must be defined inside it.
//
// Elements used (from the site's HTML):
//   button[data-testid="settingsButton"]       settings (sliders) menu
//   [data-testid="settingsMyProfileLink"]       "Your Cruiser Profile"
//   [data-testid="chatYourProfileButton"]       "Go To Edit Profile"
//   row containing "cruising status" → [role=button]   status field (shows current status)
//   ui-wheel [role=option] (.ui-wheel__item)    status options (scroll wheel, in .bottom-flyout-content)
//   [data-testid="rightActionButton"] "Done"    confirm picker
//   [data-testid="save-profile-button"]         Save
//   [role=button][aria-label="Go Back"]         leave edit screen
//   [data-testid="headerGoBackButton"]          close cruiser profile

export async function runSteps(job) {
  const T = Math.max(3000, job.timeoutMs || 10000);
  // Multitask mode: the window may be hidden or covered, where Chrome slows page timers a lot;
  // the extension's background keeps time instead.
  const sleep = (ms) =>
    job.quiet
      ? chrome.runtime.sendMessage({ type: "sleep", ms }).catch(() => new Promise((r) => setTimeout(r, ms)))
      : new Promise((r) => setTimeout(r, ms));
  const norm = (s) =>
    (s || "").replace(/[‘’`´]/g, "'").replace(/\s+/g, " ").trim().toLowerCase();
  const visible = (el) =>
    !!el && el.isConnected && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
  const all = (sel) => Array.from(document.querySelectorAll(sel)).filter(visible);
  const byTestId = (id) => all(`[data-testid="${id}"]`)[0] || null;

  const waitFor = async (fn, ms = T) => {
    const end = Date.now() + ms;
    for (;;) {
      let v = null;
      try { v = fn(); } catch { /* element vanished mid-check */ }
      if (v) return v;
      if (Date.now() >= end) return null;
      await sleep(150);
    }
  };

  // A full pointer/mouse sequence, so libraries that ignore a bare click() still react.
  const click = (el) => {
    el.scrollIntoView({ block: "center", inline: "center" });
    const r = el.getBoundingClientRect();
    const base = {
      bubbles: true, cancelable: true, composed: true, view: window, button: 0,
      clientX: r.left + r.width / 2, clientY: r.top + r.height / 2,
    };
    const ptr = { ...base, pointerId: 1, pointerType: "mouse", isPrimary: true };
    el.dispatchEvent(new PointerEvent("pointerdown", ptr));
    el.dispatchEvent(new MouseEvent("mousedown", base));
    el.dispatchEvent(new PointerEvent("pointerup", ptr));
    el.dispatchEvent(new MouseEvent("mouseup", base));
    el.dispatchEvent(new MouseEvent("click", base));
  };

  const target = norm(job.targetStatus);
  const rowLabel = norm(job.statusRowLabel || "hosting status");
  let step = "Start";
  const fail = (message) => ({ ok: false, step, message });

  // The status field has no fixed name (its text is the current status), so find it by its row label.
  const findStatusCell = () => {
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      if (!norm(n.nodeValue).includes(rowLabel)) continue;
      const host = n.parentElement;
      if (!visible(host)) continue;
      const row = host.closest("tr") || host.parentElement?.parentElement;
      const btn = row && Array.from(row.querySelectorAll('[role="button"]')).find(visible);
      if (btn) return btn;
    }
    const candidates = all('td[role="button"]').filter((td) => td.querySelector(".fa-angle-down"));
    return candidates.length === 1 ? candidates[0] : null;
  };

  // The site's own wheel: <ui-wheel> with div.ui-wheel__item[role=option] (aria-selected="true" = current).
  // Older Mobiscroll markup kept as a fallback.
  const WHEEL_ITEMS = 'ui-wheel [role="option"], .ui-wheel__item, mbsc-wheel-item, .mbsc-scroller-wheel-item';
  const wheelItems = () => all(WHEEL_ITEMS);
  // The element that takes arrow keys: the wheel's listbox viewport.
  const wheelViewport = () => all('[data-testid="wheelViewport"], ui-wheel [role="listbox"]')[0] || null;

  // Short description of an element, for error messages.
  const describe = (el) => {
    if (!el) return "nothing";
    const cls = typeof el.className === "string" ? el.className.trim().split(/\s+/).slice(0, 3).join(".") : "";
    return `<${el.tagName.toLowerCase()}${cls ? "." + cls : ""}> "${norm(el.textContent).slice(0, 40)}"`;
  };
  const selectedItem = () => wheelItems().find((i) => i.getAttribute("aria-selected") === "true") || null;

  const isText = (el, wanted) => !!el && norm(el.textContent) === wanted;

  // Select the option whose text is `wanted` in the open scroll wheel.
  const chooseInWheel = async (wanted) => {
    if (isText(selectedItem(), wanted)) return true;

    const direct = wheelItems().find((i) => isText(i, wanted));
    if (direct) {
      click(direct);
      if (await waitFor(() => isText(selectedItem(), wanted), 2500)) return true;
    }

    // Option not rendered yet (or the tap was ignored): step through the wheel with arrow keys.
    for (const [key, code] of [["ArrowDown", 40], ["ArrowUp", 38]]) {
      let last = null;
      let stuck = 0;
      for (let i = 0; i < 30; i++) {
        const cur = selectedItem() || wheelItems()[0];
        if (!cur) return false;
        const el = wheelViewport() || cur;
        el.focus();
        for (const type of ["keydown", "keyup"]) {
          el.dispatchEvent(new KeyboardEvent(type, { key, code: key, keyCode: code, which: code, bubbles: true, cancelable: true }));
        }
        await sleep(300);

        const now = selectedItem();
        if (isText(now, wanted)) return true;
        const shown = wheelItems().find((it) => isText(it, wanted));
        if (shown) {
          click(shown);
          if (await waitFor(() => isText(selectedItem(), wanted), 1500)) return true;
        }

        const txt = now ? norm(now.textContent) : null;
        if (txt === last) { if (++stuck >= 2) break; } else stuck = 0; // reached the end of the wheel
        last = txt;
      }
    }
    return false;
  };

  // Returns null when the picker is open, otherwise a reason.
  let pickerWhy = "";
  const openPicker = async () => {
    const opened = () => wheelItems().length > 0;
    let cell = null;
    for (let attempt = 1; attempt <= 3; attempt++) {
      cell = await waitFor(findStatusCell, 4000);
      if (!cell) { pickerWhy = "status field disappeared"; return false; }
      // Let the edit screen finish sliding in; a click during the animation is ignored.
      await sleep(attempt === 1 ? 500 : 1000);
      if (opened()) break;

      // 1) the cell itself, 2) its innermost part (the handler may sit on a child), 3) keyboard.
      const inner = cell.querySelector(".fa-angle-down, input, [tabindex]") || cell.firstElementChild;
      const tries = [
        () => click(cell),
        () => inner && click(inner),
        () => {
          cell.focus?.();
          for (const type of ["keydown", "keyup"]) {
            cell.dispatchEvent(new KeyboardEvent(type, { key: "Enter", code: "Enter", keyCode: 13, which: 13, bubbles: true }));
          }
        },
      ];
      tries[attempt - 1]();
      if (await waitFor(opened, Math.min(T, 3000))) break;
    }

    if (!opened()) {
      const popup = all('.bottom-flyout-content, ui-wheel, [class*="mbsc-popup"], [class*="mbsc-scroller"], [role="dialog"]')[0];
      pickerWhy = popup
        ? `a popup opened (${describe(popup)}) but no wheel items matched; the option markup has changed`
        : `clicked ${describe(cell)} but no picker appeared; wrong element, or the click was ignored`;
      return false;
    }
    await sleep(300);
    return true;
  };

  const pressDone = async () => {
    const done = await waitFor(() =>
      all('[data-testid="rightActionButton"]').find((b) => norm(b.textContent) === "done"));
    if (!done) return false;
    click(done);
    await waitFor(() => wheelItems().length === 0, 4000);
    await sleep(300);
    return true;
  };

  // Open the picker, choose `wanted`, press Done and check the field shows it. Returns an error or null.
  const setStatus = async (wanted, label) => {
    step = `Open status picker (${label})`;
    if (!(await openPicker())) return "status picker did not open: " + pickerWhy;
    step = `Choose "${label}"`;
    if (!(await chooseInWheel(wanted))) return `could not select "${label}" in the picker`;
    step = `Done (${label})`;
    if (!(await pressDone())) return "Done button not found";
    step = `Check status (${label})`;
    const ok = await waitFor(() => {
      const c = findStatusCell();
      return c && norm(c.textContent) === wanted ? c : null;
    }, 4000);
    return ok ? null : `status field did not show "${label}" after Done`;
  };

  const saveButton = (ms) => waitFor(() => byTestId("save-profile-button"), ms);

  try {
    // If a previous attempt left the edit screen open, carry on from there.
    if (!findStatusCell()) {
      step = "Open settings menu";
      if (!byTestId("settingsMyProfileLink")) {
        const settings = await waitFor(() => all('button[data-testid="settingsButton"]')[0]);
        if (!settings) return fail("Settings (sliders) button not found");
        click(settings);
      }

      step = "Open Your Cruiser Profile";
      const link = await waitFor(() => byTestId("settingsMyProfileLink"));
      if (!link) return fail("'Your Cruiser Profile' not found in the menu");
      click(link);

      step = "Open edit profile";
      const edit = await waitFor(() => byTestId("chatYourProfileButton"));
      if (!edit) return fail("'Go To Edit Profile' button not found");
      click(edit);
    }

    step = "Find status field";
    const cell = await waitFor(findStatusCell);
    if (!cell) return fail(`couldn't find the "${job.statusRowLabel}" field`);

    let how;
    if (norm(cell.textContent) !== target) {
      const err = await setStatus(target, job.targetStatus);
      if (err) return fail(err);
      how = `status set to "${job.targetStatus}"`;
    } else {
      // Already set: the site only shows Save after the status is touched.
      // 1) Open the picker and press Done without changing anything (what you'd do by hand).
      const err = await setStatus(target, job.targetStatus);
      if (err) return fail(err);
      how = "status was already set (re-confirmed)";

      // 2) Still no Save? Switch to a neighbouring option, then back.
      if (!(await saveButton(2500))) {
        step = "Open status picker (switch away)";
        if (!(await openPicker())) return fail("status picker did not open: " + pickerWhy);
        const items = wheelItems();
        const sel = selectedItem();
        const idx = sel ? items.indexOf(sel) : -1;
        const other = [items[idx + 1], items[idx - 1], ...items]
          .find((i) => i && norm(i.textContent) && norm(i.textContent) !== target);
        if (!other) return fail("no other option found in the picker");
        const otherText = norm(other.textContent);
        const otherLabel = other.textContent.trim();

        step = `Choose "${otherLabel}" (temporary)`;
        if (!(await chooseInWheel(otherText))) return fail(`could not select "${otherLabel}" temporarily`);
        step = "Done (temporary)";
        if (!(await pressDone())) return fail("Done button not found");

        const back = await setStatus(target, job.targetStatus);
        if (back) return fail(back + ` (status may now be "${otherLabel}"; not saved)`);
        how = "status was already set (switched away and back)";
      }
    }

    step = "Save";
    const save = await saveButton(Math.min(T, 5000));
    if (!save) return fail("Save button did not appear");
    click(save);
    const confirmed = await waitFor(() => {
      const b = byTestId("save-profile-button");
      return !b || b.disabled;
    }, T);

    step = "Close edit screen";
    const back1 = await waitFor(() => all('[role="button"][aria-label="Go Back"]')[0], 4000);
    if (back1) {
      click(back1);
      await sleep(700);
    }

    step = "Close cruiser profile";
    const back2 = await waitFor(() => byTestId("headerGoBackButton"), 4000);
    if (back2) click(back2);

    return {
      ok: true,
      step: "Finished",
      message: `${how}; ` +
        (confirmed ? "saved" : "Save clicked (no confirmation seen)"),
    };
  } catch (e) {
    return fail("script error: " + ((e && e.message) || e));
  }
}
