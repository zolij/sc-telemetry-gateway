"use strict";

const MfdWorkspace = (() => {
  const $ = id => document.getElementById(id);
  const limits = { profiles: 32, pages: 16, tiles: 64, bytes: 512 * 1024 };
  const colors = ["neutral", "green", "amber", "red", "blue"];
  let getToken, repaint;
  let catalog = [], summaries = [], saved = null, draft = null, pageId = null;
  let cards = new Map(), busy = false, polling = false, etag = null, epoch = 0, viewVersion = 0;
  let tileId = null, preference = null;
  let preferenceWarning = "";

  function node(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }
  function notice(message, error = false) {
    $("workspace-message").textContent = [message, preferenceWarning].filter(Boolean).join(" ");
    $("workspace-message").classList.toggle("error", error || !!preferenceWarning);
  }
  const clone = value => JSON.parse(JSON.stringify(value));
  function uuid() {
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 15) | 64;
    bytes[8] = (bytes[8] & 63) | 128;
    const hex = [...bytes].map(value => value.toString(16).padStart(2, "0")).join("");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }
  const readable = MfdPresentation.readable;
  function label(path) {
    return path.split(".").filter(part => !["state", "flight", "instruments", "actor", "vehicle"].includes(part))
      .map(readable).join(" / ").slice(0, 80);
  }
  const profile = () => draft ?? saved;
  const page = () => profile()?.pages.find(item => item.id === pageId);
  const dirty = () => draft !== null;
  function canLeave() {
    return !busy && (!dirty() || window.confirm("Discard your unsaved MFD changes? Export your draft first if you want to keep it."));
  }

  async function api(path, options = {}) {
    const requestEpoch = epoch;
    const headers = { Authorization: "Bearer " + getToken(), ...options.headers };
    if (options.body !== undefined) headers["Content-Type"] = "application/json";
    const response = await fetch("/api/mfd/" + path, { ...options, headers, signal: AbortSignal.timeout(15000) });
    if (requestEpoch !== epoch) throw new Error("The display session changed. Retry the operation.");
    if (response.status === 304) return { unchanged: true };
    if (!response.ok) {
      const result = await response.json();
      const error = new Error(result.message ?? "Gateway request failed (" + response.status + ").");
      error.status = response.status;
      throw error;
    }
    return { data: response.status === 204 ? null : await response.json(), etag: response.headers.get("ETag") };
  }
  function controls() {
    const editing = draft !== null;
    $("editor-toolbar").hidden = !editing;
    $("field-library").hidden = !editing;
    $("workspace-message").setAttribute("aria-busy", String(busy));
    for (const button of document.querySelectorAll("#dashboard [data-action]")) button.disabled = busy;
    $("profile-select").disabled = busy;
    $("unpair-button").disabled = busy;
    for (const id of ["edit-profile", "copy-profile", "delete-profile"]) $(id).disabled = busy || !saved || editing;
    $("copy-profile").disabled ||= summaries.length >= limits.profiles;
    $("export-profile").disabled = busy || !profile();
    $("new-profile").disabled = busy || editing || summaries.length >= limits.profiles;
    $("import-profile").disabled = busy || editing || summaries.length >= limits.profiles;
    $("add-page").disabled = busy || (draft?.pages.length ?? 0) >= limits.pages;
    $("delete-page").disabled = busy || (draft?.pages.length ?? 0) <= 1;
    const index = draft?.pages.findIndex(item => item.id === pageId) ?? -1;
    $("page-before").disabled = busy || index <= 0;
    $("page-after").disabled = busy || index < 0 || index >= (draft?.pages.length ?? 0) - 1;
    document.querySelector(".workspace-body").classList.toggle("editing", editing);
    for (const input of document.querySelectorAll("#editor-toolbar input")) input.disabled = busy;
    for (const button of document.querySelectorAll(".tile-controls button, #field-results button, #page-navigation button"))
      button.disabled = busy;
  }
  async function run(operation) {
    if (busy) return;
    const active = document.activeElement;
    const focusTile = active?.closest("[data-tile-id]")?.dataset.tileId;
    const focusLabel = active?.getAttribute("aria-label");
    viewVersion++;
    busy = true;
    controls();
    try { await operation(); }
    catch (error) {
      notice(error.message, true);
      if (error.status === 412 && draft) $("conflict-actions").hidden = false;
    } finally {
      busy = false; controls();
      if (focusTile && focusLabel && !$("tile-dialog").open)
        $("custom-cards").querySelector(`[data-tile-id="${CSS.escape(focusTile)}"] [aria-label="${CSS.escape(focusLabel)}"]`)?.focus();
    }
  }
  function remember() {
    if (!saved || draft) return;
    preference = { profile: saved.id, page: pageId };
    try { localStorage.setItem("mfd-active-view", JSON.stringify(preference)); }
    catch {
      preferenceWarning = "This browser cannot remember the active view. Profiles are still saved on the PC.";
      notice("");
    }
  }
  function renderProfiles() {
    const select = $("profile-select");
    select.replaceChildren(new Option("Choose a profile", ""));
    for (const item of [...summaries].sort((a, b) => a.name.localeCompare(b.name)))
      select.append(new Option(item.name, item.id));
    if (draft && !saved) {
      const unsaved = new Option(draft.name + " (unsaved)", "draft");
      unsaved.disabled = true;
      select.append(unsaved);
    }
    select.value = saved?.id ?? (draft ? "draft" : "");
  }
  function render() {
    renderProfiles();
    const current = profile();
    if (current && !current.pages.some(item => item.id === pageId)) pageId = current.pages[0].id;
    $("page-navigation").replaceChildren();
    for (const [index, item] of (current?.pages ?? []).entries()) {
      const button = node("button", "page-button", String(index + 1).padStart(2, "0") + " / " + item.name);
      button.type = "button";
      button.setAttribute("aria-pressed", String(item.id === pageId));
      button.addEventListener("click", () => {
        if (busy || item.id === pageId) return;
        if (draft && !window.confirm("Keep editing and switch pages? Changes remain in this unsaved draft.")) return;
        viewVersion++;
        pageId = item.id;
        render();
        remember();
      });
      $("page-navigation").append(button);
    }
    $("page-title").textContent = page()?.name ?? "Your flight deck";
    $("display-mode").textContent = draft ? "LAYOUT EDITOR / UNSAVED DRAFT" : "LIVE INSTRUMENTS";
    if (draft) {
      $("profile-name").value = draft.name;
      $("page-name").value = page().name;
    }
    $("empty-display").hidden = !!page()?.tiles.length;
    $("empty-display").querySelector("h3").textContent = !current ? "Create your first MFD." : draft ? "Choose your instruments." : "This page is a blank canvas.";
    $("empty-display").querySelector("p").textContent = !current ? "No presets. Choose the data that matters to you." :
      draft ? "Add a shared field from the data library." : "Select EDIT to add your own telemetry tiles.";
    cards = new Map();
    $("custom-cards").replaceChildren();
    for (const tile of page()?.tiles ?? []) {
      const card = node("article", "instrument size-" + tile.size);
      card.dataset.tileId = tile.id;
      card.dataset.widget = tile.widget;
      const value = node(tile.widget === "list" ? "div" : "strong", "instrument-value", "Unknown");
      const meta = node("span", "instrument-meta", "Waiting for telemetry");
      card.append(node("span", "instrument-label", tile.label), value, meta);
      if (draft) addTileControls(card, tile);
      cards.set(tile.id, { tile, card, value, meta });
      $("custom-cards").append(card);
    }
    controls();
    repaint?.();
  }

  function paint(snapshot, online, elapsed, ageText) {
    const selected = new Set(snapshot?.selected_fields ?? catalog.map(item => item.path));
    for (const { tile, card, value, meta } of cards.values()) {
      const field = snapshot?.fields?.[tile.field];
      const definition = catalog.find(item => item.path === tile.field);
      const mode = !tile.format || tile.format === "auto" ? definition?.display_format ?? "number" : tile.format;
      const maximum = selected.has(definition?.maximum_field) ? snapshot?.fields?.[definition.maximum_field] : null;
      const maximumValue = MfdPresentation.maximumValue(field, maximum);
      const needsMaximum = mode === "percent_max" && field?.value != null && !MfdPresentation.validMaximum(maximumValue);
      const history = tile.field === "events.events.type";
      card.dataset.color = "neutral";
      if (!selected.has(tile.field) || !field) {
        delete value.dataset.signature;
        value.textContent = !selected.has(tile.field) ? "Not selected" : "Unknown";
        meta.textContent = !selected.has(tile.field) ? "Enable this field in PC settings" : online ? "No value received" : "Waiting / offline";
        card.dataset.state = "unavailable";
        continue;
      }
      const age = Number.isFinite(field.age_ms) ? field.age_ms + elapsed : NaN;
      const state = !online ? "OFFLINE / LAST KNOWN" : field.last_known ? "LAST KNOWN" : field.value === null ? "UNKNOWN" :
        history ? "HISTORY" : !Number.isFinite(age) ? "AGE UNKNOWN" : age >= 2000 ? "STALE" : "LIVE";
      const rule = tile.widget === "status" && field.value !== null
        ? tile.rules.find(item => item.value === String(field.value)) : null;
      if (tile.widget === "list" && Array.isArray(field.value)) {
        const signature = JSON.stringify([field.value, mode, tile.decimals, definition?.type]);
        if (value.dataset.signature !== signature) {
          value.replaceChildren();
          for (const item of field.value) value.append(node("div", "list-value", MfdPresentation.format(item, tile.decimals, mode, null, definition?.type)));
          if (!field.value.length) value.textContent = "None";
          value.dataset.signature = signature;
        }
      } else {
        value.textContent = rule?.label ?? MfdPresentation.format(field.value, tile.decimals, mode, maximumValue, definition?.type);
        delete value.dataset.signature;
      }
      if (rule && online && !field.last_known && age < 2000) card.dataset.color = rule.color;
      meta.textContent = state + " / " + ageText(age);
      if (history && snapshot.events_dropped > 0) {
        meta.textContent += " / " + snapshot.events_dropped + " EARLIER EVENTS DROPPED";
        card.dataset.color = "amber";
      }
      if (needsMaximum && !rule)
        meta.textContent = "PERCENTAGE UNAVAILABLE / " + meta.textContent +
          " / Enable and receive matching " + (definition?.maximum_field ?? "maximum field");
      card.dataset.state = !online ? "offline" : field.last_known ? "last-known" :
        field.value === null || needsMaximum && !rule ? "unavailable" : !history && (!Number.isFinite(age) || age >= 2000) ? "stale" : "live";
    }
  }

  async function refresh(force = false) {
    const version = viewVersion;
    const result = await api("profiles", { headers: !force && etag ? { "If-None-Match": etag } : {} });
    if (version !== viewVersion) return;
    if (result.unchanged) return;
    summaries = result.data;
    etag = result.etag;
    renderProfiles();
    if (saved) {
      const latest = summaries.find(item => item.id === saved.id);
      if (!latest || latest.revision !== saved.revision) {
        if (draft) {
          notice("This profile changed or was deleted on another display. Your draft has not been overwritten.", true);
          $("conflict-actions").hidden = false;
        } else if (!latest) {
          saved = null;
          pageId = null;
          render();
          notice("The active profile was deleted on another display. Choose a profile.", true);
        } else {
          const latestProfile = (await api("profiles/" + saved.id)).data;
          if (version !== viewVersion) return;
          saved = latestProfile;
          render();
          notice("Profile updated from the PC.");
        }
      }
    }
    controls();
  }
  async function selectProfile(id) {
    const next = id ? (await api("profiles/" + id)).data : null;
    saved = next;
    draft = null;
    pageId = preference?.profile === id ? preference.page : next?.pages[0].id;
    $("conflict-actions").hidden = true;
    render();
    remember();
    notice(next ? "Profile loaded from PC." : "Create a profile or choose an existing display.");
  }
  async function start() {
    await run(async () => {
      catalog = (await api("fields")).data;
      await refresh(true);
      if (!saved && preference?.profile && summaries.some(item => item.id === preference.profile))
        await selectProfile(preference.profile);
      renderLibrary();
      render();
    });
  }
  function reset() {
    epoch++;
    saved = draft = null;
    pageId = null;
    summaries = catalog = [];
    etag = null;
    $("conflict-actions").hidden = true;
    $("tile-dialog").close();
    render();
  }

  // Editor functions are kept separate from the telemetry paint path.
  function addTileControls(card, tile) {
    const controls = node("div", "tile-controls");
    const handle = node("button", "drag-handle", "MOVE / DRAG OR ARROW KEYS");
    handle.type = "button";
    handle.setAttribute("aria-label", "Move " + tile.label + " with drag or arrow keys");
    handle.addEventListener("keydown", event => {
      if (["ArrowLeft", "ArrowUp", "ArrowRight", "ArrowDown"].includes(event.key)) {
        event.preventDefault();
        run(() => moveTile(tile.id, ["ArrowLeft", "ArrowUp"].includes(event.key) ? -1 : 1));
      }
    });
    let drag = null;
    handle.addEventListener("pointerdown", event => {
      if (busy || event.button !== 0) return;
      drag = { pointer: event.pointerId, x: event.clientX, y: event.clientY, moved: false, target: null };
      handle.setPointerCapture(event.pointerId);
    });
    handle.addEventListener("pointermove", event => {
      if (!drag || drag.pointer !== event.pointerId) return;
      if (Math.hypot(event.clientX - drag.x, event.clientY - drag.y) < 8 && !drag.moved) return;
      drag.moved = true;
      card.classList.add("dragging");
      for (const item of cards.values()) item.card.classList.remove("drop-target");
      const target = document.elementFromPoint(event.clientX, event.clientY)?.closest("[data-tile-id]");
      drag.target = target?.dataset.tileId ?? null;
      if (drag.target && drag.target !== tile.id) target.classList.add("drop-target");
    });
    const finish = event => {
      if (!drag || drag.pointer !== event.pointerId) return;
      const completed = drag;
      drag = null;
      card.classList.remove("dragging");
      for (const item of cards.values()) item.card.classList.remove("drop-target");
      if (handle.hasPointerCapture(event.pointerId)) handle.releasePointerCapture(event.pointerId);
      if (event.type !== "pointerup" || !completed.moved || !completed.target || completed.target === tile.id) return;
      run(() => {
        const tiles = page().tiles;
        const from = tiles.findIndex(item => item.id === tile.id);
        const to = tiles.findIndex(item => item.id === completed.target);
        if (from < 0 || to < 0) return;
        tiles.splice(to, 0, tiles.splice(from, 1)[0]);
        render();
        notice("Tile moved to position " + (to + 1) + ". Save to keep the new layout.");
      });
    };
    handle.addEventListener("pointerup", finish);
    handle.addEventListener("pointercancel", finish);
    controls.append(handle);
    for (const [text, title, action] of [
      ["SETUP", "Configure " + tile.label, () => openTile(tile)],
      ["LEFT", "Move " + tile.label + " earlier", () => moveTile(tile.id, -1)],
      ["RIGHT", "Move " + tile.label + " later", () => moveTile(tile.id, 1)],
      ["COPY", "Duplicate " + tile.label, () => {
        if (page().tiles.length >= limits.tiles) throw new Error("This page has reached the 64 tile limit.");
        page().tiles.push({ ...clone(tile), id: uuid() }); render();
      }],
      ["REMOVE", "Remove " + tile.label, () => {
        if (window.confirm("Remove this tile?")) { page().tiles = page().tiles.filter(item => item.id !== tile.id); render(); }
      }]
    ]) {
      const button = node("button", "", text);
      button.type = "button"; button.title = title; button.setAttribute("aria-label", title);
      button.addEventListener("click", () => run(action));
      controls.append(button);
    }
    card.append(controls);
  }
  function moveTile(id, delta) {
    const tiles = page().tiles;
    const index = tiles.findIndex(item => item.id === id);
    const next = index + delta;
    if (next >= 0 && next < tiles.length) {
      [tiles[index], tiles[next]] = [tiles[next], tiles[index]];
      render();
      notice("Tile moved to position " + (next + 1) + ". Save to keep the new layout.");
    }
  }
  function renderLibrary() {
    const query = $("field-search").value.toLowerCase().trim();
    $("field-results").replaceChildren();
    let group = null, section = null;
    for (const field of [...catalog].sort((a, b) => a.path.localeCompare(b.path))) {
      if (query && !(field.path + " " + label(field.path)).toLowerCase().includes(query)) continue;
      const root = field.path.split(".")[0];
      if (root !== group) {
        group = root;
        section = node("div", "field-group");
        section.append(node("h3", "", readable(root)));
        $("field-results").append(section);
      }
      const button = node("button", "field-item");
      button.type = "button";
      button.disabled = busy;
      button.append(node("strong", "", label(field.path)), node("span", "", field.path));
      button.addEventListener("click", () => {
        if (busy) return;
        if (page().tiles.length >= limits.tiles) { notice("This page has reached the 64 tile limit.", true); return; }
        openTile({ id: uuid(), field: field.path, label: label(field.path), widget: field.repeated ? "list" : "value", size: "normal", decimals: 2, format: "auto", rules: [] }, true);
      });
      section.append(button);
    }
    if (!$("field-results").childElementCount)
      $("field-results").append(node("p", "library-note", catalog.length ? "No matching fields." : "Enable shared fields in gateway settings, then restart collection."));
  }
  function widgetOptions() {
    const field = catalog.find(item => item.path === $("tile-field").value);
    const repeated = field?.repeated ?? page()?.tiles.find(item => item.id === tileId)?.widget === "list";
    for (const option of $("tile-widget").options) option.disabled = repeated ? option.value !== "list" : option.value === "list";
    if ($("tile-widget").selectedOptions[0].disabled) $("tile-widget").value = repeated ? "list" : "value";
    $("status-settings").hidden = $("tile-widget").value !== "status";
    for (const option of $("tile-format").options)
      option.disabled = !!field && (
        !["auto", "number"].includes(option.value) && !field.numeric ||
        option.value === "percent_max" && !field.maximum_field
      );
    if ($("tile-format").selectedOptions[0].disabled) $("tile-format").value = "auto";
    const chosen = $("tile-format").value;
    const mode = chosen === "auto" ? field?.display_format ?? "number" : chosen;
    $("format-note").textContent = mode === "percent_max"
      ? "Percentage needs a positive, matching " + (field?.maximum_field ?? "maximum field") + ". Enable that field in PC settings too."
      : mode === "percent_fraction" ? "A raw value of 0.75 is displayed as 75%."
      : mode === "percent" ? "A raw value of 75 is displayed as 75%."
      : "Values are displayed without percentage scaling. Status rules always match raw values.";
  }
  function addRule(rule = { value: "", label: "", color: "neutral" }) {
    if ($("status-rules").childElementCount >= 32) { $("tile-message").textContent = "At most 32 status rules are allowed."; return; }
    const row = node("div", "status-rule");
    for (const [name, text, maximum] of [["value", rule.value, 128], ["label", rule.label, 80]]) {
      const input = node("input");
      input.value = text; input.maxLength = maximum; input.placeholder = name === "value" ? "Raw value" : "Display label";
      input.setAttribute("aria-label", name === "value" ? "Status raw value" : "Status label");
      input.dataset.rule = name;
      row.append(input);
    }
    const select = node("select");
    select.setAttribute("aria-label", "Status color");
    for (const color of colors) select.append(new Option(readable(color), color));
    select.value = rule.color;
    const remove = node("button", "", "REMOVE");
    remove.type = "button"; remove.addEventListener("click", () => row.remove());
    row.append(select, remove); $("status-rules").append(row);
  }
  function openTile(tile, adding = false) {
    tileId = adding ? null : tile.id;
    $("tile-dialog").dataset.newId = tile.id;
    $("tile-field").replaceChildren();
    for (const field of catalog) $("tile-field").append(new Option(label(field.path) + " / " + field.path, field.path));
    if (!catalog.some(field => field.path === tile.field)) $("tile-field").append(new Option(tile.field + " (not selected on PC)", tile.field));
    $("tile-field").value = tile.field;
    $("tile-label").value = tile.label; $("tile-widget").value = tile.widget;
    $("tile-size").value = tile.size; $("tile-decimals").value = tile.decimals;
    $("tile-format").value = tile.format ?? "auto";
    $("status-rules").replaceChildren();
    for (const rule of tile.rules) addRule(rule);
    $("tile-message").textContent = "";
    widgetOptions();
    $("tile-dialog").showModal();
  }

  function validate(value) {
    const text = (value, max) => typeof value === "string" && value.trim().length > 0 && value.length <= max && !/[\u0000-\u001f\u007f]/.test(value);
    const keys = (object, allowed) => object && typeof object === "object" && !Array.isArray(object) && Object.keys(object).every(key => allowed.includes(key));
    if (!keys(value, ["schema_version", "id", "revision", "name", "pages"]) || value.schema_version !== 1 || !text(value.name, 80) ||
        !Array.isArray(value.pages) || value.pages.length < 1 || value.pages.length > limits.pages)
      throw new Error("Invalid profile schema, name or page count.");
    const ids = new Set();
    const identity = id => {
      if (typeof id !== "string" || !/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(id) ||
          id === "00000000-0000-0000-0000-000000000000" || ids.has(id.toLowerCase())) throw new Error("Page and tile IDs must be unique UUIDs.");
      ids.add(id.toLowerCase());
    };
    for (const item of value.pages) {
      if (!keys(item, ["id", "name", "tiles"]) || !text(item.name, 80) || !Array.isArray(item.tiles) || item.tiles.length > limits.tiles)
        throw new Error("Invalid page name or tile count.");
      identity(item.id);
      for (const tile of item.tiles) {
        if (!keys(tile, ["id", "field", "label", "widget", "size", "decimals", "format", "rules"]) || !text(tile.field, 1024) || !text(tile.label, 80) ||
            !["value", "status", "list"].includes(tile.widget) || !["normal", "wide", "large"].includes(tile.size) ||
            !["auto", "number", "percent_fraction", "percent", "percent_max"].includes(tile.format === undefined ? "auto" : tile.format) ||
            !Number.isInteger(tile.decimals) || tile.decimals < 0 || tile.decimals > 6 || !Array.isArray(tile.rules) ||
            tile.rules.length > 32 || tile.widget !== "status" && tile.rules.length)
          throw new Error("Invalid tile settings.");
        identity(tile.id);
        const field = catalog.find(field => field.path === tile.field);
        if (field && field.repeated !== (tile.widget === "list")) throw new Error("Widget is incompatible with its field.");
        if (field && tile.format && !["auto", "number"].includes(tile.format) && (
          !field.numeric || tile.format === "percent_max" && !field.maximum_field
        )) throw new Error("Display format is incompatible with its field.");
        const values = new Set();
        for (const rule of tile.rules) {
          if (!keys(rule, ["value", "label", "color"]) || !text(rule.value, 128) || !text(rule.label, 80) ||
              !colors.includes(rule.color) || values.has(rule.value)) throw new Error("Invalid or duplicate status rule.");
          values.add(rule.value);
        }
      }
    }
    if (new TextEncoder().encode(JSON.stringify(value)).length > limits.bytes) throw new Error("Profiles must not exceed 512 KiB.");
  }

  async function save(asCopy = false) {
    validate(draft);
    const pageIndex = draft.pages.findIndex(item => item.id === pageId);
    const creating = asCopy || !saved;
    const result = await api("profiles" + (creating ? "" : "/" + saved.id), {
      method: creating ? "POST" : "PUT",
      headers: creating ? {} : { "If-Match": '"' + saved.revision + '"' },
      body: JSON.stringify(draft)
    });
    saved = result.data; draft = null; pageId = saved.pages[pageIndex].id;
    $("conflict-actions").hidden = true;
    render(); remember();
    await refresh(true);
    notice("Profile saved on the PC.");
  }
  function exportProfile() {
    const exported = clone(profile());
    delete exported.id; delete exported.revision;
    validate(exported);
    const url = URL.createObjectURL(new Blob([JSON.stringify(exported)], { type: "application/json" }));
    const link = node("a");
    link.href = url; link.download = "mfd-profile.json";
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    notice(draft ? "Unsaved draft exported." : "Profile exported.");
  }

  function init(tokenReader, paintDisplay) {
    getToken = tokenReader; repaint = paintDisplay;
    try { preference = JSON.parse(localStorage.getItem("mfd-active-view") ?? "null"); }
    catch {
      preferenceWarning = "This browser could not read its saved view preference. PC profiles are unaffected.";
      notice("");
    }
    $("profile-select").addEventListener("change", () => {
      const id = $("profile-select").value;
      if (!canLeave()) { renderProfiles(); return; }
      run(() => selectProfile(id));
    });
    $("new-profile").addEventListener("click", () => run(() => {
      draft = { schema_version: 1, name: "My MFD", pages: [{ id: uuid(), name: "Page 1", tiles: [] }] };
      saved = null; pageId = draft.pages[0].id;
      render(); notice("Build your display, then SAVE TO PC.");
    }).then(() => { if (draft) { $("profile-name").focus(); $("profile-name").select(); } }));
    $("edit-profile").addEventListener("click", () => run(async () => {
      catalog = (await api("fields")).data;
      saved = (await api("profiles/" + saved.id)).data;
      draft = clone(saved);
      renderLibrary(); render(); notice("Editing a local draft. Other displays keep the saved layout.");
    }));
    $("save-profile").addEventListener("click", () => run(() => save()));
    $("save-copy").addEventListener("click", () => run(() => save(true)));
    $("reload-profile").addEventListener("click", () => {
      if (canLeave()) run(async () => {
        await refresh(true);
        await selectProfile(summaries.some(item => item.id === saved?.id) ? saved.id : "");
      });
    });
    $("cancel-edit").addEventListener("click", () => {
      if (!canLeave()) return;
      run(async () => {
        draft = null; $("conflict-actions").hidden = true;
        render();
        await refresh(true); render(); notice("Draft discarded.");
      });
    });
    $("copy-profile").addEventListener("click", () => run(async () => {
      const copy = clone(saved); copy.name = (copy.name + " copy").slice(0, 80);
      saved = (await api("profiles", { method: "POST", body: JSON.stringify(copy) })).data;
      pageId = saved.pages[0].id; await refresh(true); render(); remember(); notice("Profile duplicated.");
    }));
    $("delete-profile").addEventListener("click", () => {
      if (!window.confirm("Delete this shared profile from the PC? All displays will lose access to it.")) return;
      run(async () => {
        await api("profiles/" + saved.id, { method: "DELETE", headers: { "If-Match": '"' + saved.revision + '"' } });
        saved = null; pageId = null; await refresh(true); render(); notice("Profile deleted.");
      });
    });
    $("profile-name").addEventListener("input", event => { if (draft) draft.name = event.target.value; });
    $("page-name").addEventListener("input", event => {
      if (!draft) return;
      page().name = event.target.value;
      $("page-title").textContent = page().name;
      const index = draft.pages.findIndex(item => item.id === pageId);
      $("page-navigation").children[index].textContent = String(index + 1).padStart(2, "0") + " / " + page().name;
    });
    $("add-page").addEventListener("click", () => run(() => {
      const next = { id: uuid(), name: "Page " + (draft.pages.length + 1), tiles: [] };
      draft.pages.push(next); pageId = next.id; render();
    }));
    for (const [id, delta] of [["page-before", -1], ["page-after", 1]])
      $(id).addEventListener("click", () => run(() => {
        const index = draft.pages.findIndex(item => item.id === pageId);
        [draft.pages[index], draft.pages[index + delta]] = [draft.pages[index + delta], draft.pages[index]];
        render();
      }));
    $("delete-page").addEventListener("click", () => {
      if (!window.confirm("Delete this page and its tiles from the draft?")) return;
      run(() => { draft.pages = draft.pages.filter(item => item.id !== pageId); pageId = draft.pages[0].id; render(); });
    });
    $("field-search").addEventListener("input", renderLibrary);
    $("tile-field").addEventListener("change", widgetOptions);
    $("tile-widget").addEventListener("change", widgetOptions);
    $("tile-format").addEventListener("change", widgetOptions);
    $("add-rule").addEventListener("click", () => addRule());
    $("cancel-tile").addEventListener("click", () => $("tile-dialog").close());
    $("tile-form").addEventListener("submit", event => {
      event.preventDefault();
      try {
        const tile = {
          id: tileId ?? $("tile-dialog").dataset.newId, field: $("tile-field").value,
          label: $("tile-label").value.trim(), widget: $("tile-widget").value,
          size: $("tile-size").value, decimals: Number($("tile-decimals").value), format: $("tile-format").value,
          rules: $("tile-widget").value === "status" ? [...$("status-rules").children].map(row => ({
            value: row.querySelector('[data-rule="value"]').value,
            label: row.querySelector('[data-rule="label"]').value.trim(), color: row.querySelector("select").value
          })) : []
        };
        const next = clone(draft);
        const target = next.pages.find(item => item.id === pageId);
        if (tileId) target.tiles[target.tiles.findIndex(item => item.id === tileId)] = tile;
        else target.tiles.push(tile);
        validate(next); draft = next;
        $("tile-dialog").close(); render();
      } catch (error) { $("tile-message").textContent = error.message; }
    });
    $("export-profile").addEventListener("click", () => run(exportProfile));
    $("import-profile").addEventListener("click", () => $("import-file").click());
    $("import-file").addEventListener("change", event => {
      const file = event.target.files[0];
      event.target.value = "";
      if (!file) return;
      run(async () => {
        if (file.size > limits.bytes) throw new Error("Import must not exceed 512 KiB.");
        const imported = JSON.parse(await file.text());
        validate(imported);
        const missing = imported.pages.flatMap(item => item.tiles).some(tile => !catalog.some(field => field.path === tile.field));
        if (missing && !window.confirm("Some imported fields are not enabled on this PC. They will be unavailable. Continue without enabling them?")) return;
        saved = (await api("profiles", { method: "POST", body: JSON.stringify(imported) })).data;
        draft = null; pageId = saved.pages[0].id;
        await refresh(true); render(); remember(); notice("Imported as a new profile.");
      });
    });
    window.addEventListener("beforeunload", event => {
      if (dirty()) { event.preventDefault(); event.returnValue = ""; }
    });
    setInterval(async () => {
      if (!getToken() || busy || polling) return;
      polling = true;
      try { await refresh(); }
      catch (error) { notice(error.message, true); }
      finally { polling = false; }
    }, 5000);
    render();
  }
  return { init, start, reset, canLeave, paint };
})();
