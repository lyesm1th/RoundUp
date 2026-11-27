/* RoundUp shared workspace — optional native Cloudflare bridge UI.
   It never replaces the local GeoZamir ZIP/route/photo engine. */
(() => {
  "use strict";
  const tools = document.getElementById("toolsMenu");
  const bridge = window.RoundUpBridge;
  if (!tools || !bridge) return;

  const state = {
    user: null, objects: [], users: [], selected: null, visits: [], messages: [],
    assignments: [], opened: false, polling: false, notificationAfter: new Date().toISOString(),
    notificationAfterId: "", notifyCount: 0
  };
  const pending = new Map();
  let serial = 0;
  const native = window.chrome && window.chrome.webview;
  const el = (tag, cls, label) => {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (label !== undefined) node.textContent = label;
    return node;
  };
  const btn = (label, onClick, cls = "") => {
    const node = el("button", "ru-cloud-btn " + cls, label);
    node.type = "button";
    node.addEventListener("click", onClick);
    return node;
  };
  const field = (label, placeholder, type = "text") => {
    const wrapper = el("label", "ru-cloud-field");
    wrapper.append(el("span", "", label));
    const input = el("input");
    input.type = type;
    input.placeholder = placeholder;
    wrapper.append(input);
    return { wrapper, input };
  };
  const safeText = value => String(value ?? "");
  const formDate = () => new Date(Date.now() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 10);
  const alertLine = el("div", "ru-cloud-alert");
  function notice(text, error = false) {
    alertLine.textContent = text;
    alertLine.className = "ru-cloud-alert" + (error ? " is-error" : "");
  }
  function request(action, extra = {}) {
    if (!native) return Promise.reject(new Error("Спільний режим працює у Windows-програмі RoundUp, не у звичайному браузері."));
    const requestId = "ru-cloud-" + (++serial);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        pending.delete(requestId);
        reject(new Error("Час очікування сервера вичерпано."));
      }, 28000);
      pending.set(requestId, { resolve, reject, timer });
      native.postMessage(Object.assign({ channel: "roundup-workspace", action, requestId }, extra));
    });
  }
  native?.addEventListener("message", event => {
    const answer = event.data;
    if (!answer || answer.channel !== "roundup-workspace-result") return;
    const waiter = pending.get(answer.requestId);
    if (!waiter) return;
    pending.delete(answer.requestId);
    clearTimeout(waiter.timer);
    if (answer.ok) waiter.resolve(answer.data);
    else waiter.reject(new Error(answer.error || "Сервер недоступний"));
  });
  const api = (method, path, body) => request("api", { method, path, body });

  const trigger = btn("☁ Спільне середовище", () => {
    tools.hidden = true;
    openPanel();
  });
  trigger.id = "roundupSharedWorkspaceBtn";
  trigger.className = "";
  const help = document.getElementById("helpBtn");
  (help || tools.lastElementChild).insertAdjacentElement("afterend", trigger);

  const shade = el("div", "ru-cloud-backdrop");
  const panel = el("section", "ru-cloud-panel");
  panel.setAttribute("role", "dialog");
  panel.setAttribute("aria-modal", "true");
  panel.setAttribute("aria-label", "Спільне середовище RoundUp");
  shade.append(panel);
  shade.addEventListener("click", event => { if (event.target === shade) closePanel(); });
  const closePanel = () => { state.opened = false; shade.remove(); };
  document.addEventListener("keydown", e => { if (e.key === "Escape" && state.opened) closePanel(); });

  function heading(subtitle = "") {
    const top = el("div", "ru-cloud-top");
    const title = el("div", "");
    title.append(el("span", "ru-cloud-eyebrow", "ROUNDUP · СПІЛЬНА РОБОТА"));
    title.append(el("h2", "", subtitle || "Карта команди"));
    top.append(title, btn("×", closePanel, "ru-cloud-close"));
    panel.append(top);
    panel.append(alertLine);
  }
  async function busy(button, task) {
    button.disabled = true;
    try { await task(); }
    catch (error) { notice(error.message || "Операція не вдалася", true); }
    finally { button.disabled = false; }
  }
  function renderSignedOut(expired = false) {
    panel.replaceChildren();
    heading("Вхід до робочої бази");
    panel.append(el("p", "ru-cloud-muted", "Локальні ZIP, фото й маршрути залишаються на комп'ютері. Підключення до спільної бази — окремий режим."));
    if (expired) notice("Ключ доступу відкликаний. Отримайте новий у керівника.", true);
    const key = field("Персональний ключ доступу", "ru_...", "password");
    panel.append(key.wrapper);
    const login = btn("Увійти", () => busy(login, async () => {
      const result = await request("login", { token: key.input.value.trim() });
      state.user = result.user;
      key.input.value = "";
      await loadObjects();
      renderSignedIn();
    }), "is-primary");
    panel.append(login);
    const details = el("details", "ru-cloud-setup");
    details.append(el("summary", "", "Перший запуск: створити керівника"));
    const name = field("Ім'я керівника", "Керівник");
    const setupSecret = field("Початковий секрет RoundUp", "Секрет із налаштувань GitHub", "password");
    details.append(name.wrapper, setupSecret.wrapper);
    const create = btn("Створити профіль керівника", () => busy(create, async () => {
      const response = await request("setup", {
        displayName: name.input.value.trim() || "Керівник",
        setupSecret: setupSecret.input.value.trim()
      });
      setupSecret.input.value = "";
      state.user = response.user;
      await loadObjects();
      renderSignedIn();
      notice("Профіль керівника створений. Ключ збережений у захищеному сховищі цього користувача Windows.");
    }));
    details.append(create);
    panel.append(details);
  }
  async function loadObjects() {
    let after = "", combined = [];
    for (let page = 0; page < 10; page++) {
      const data = await api("GET", "/v1/objects" + (after ? "?after=" + encodeURIComponent(after) : ""));
      combined = combined.concat(data.objects || []);
      if (!data.nextAfter) break;
      after = data.nextAfter;
    }
    state.objects = combined;
    if (state.user?.role === "admin") {
      const result = await api("GET", "/v1/users");
      state.users = (result.users || []).filter(u => u.role === "manager" && !!u.active);
    } else state.users = [];
    syncPins();
  }
  function renderSignedIn() {
    panel.replaceChildren();
    heading(state.selected ? "Картка об'єкта" : "Спільна карта команди");
    if (state.selected) { void renderObjectDetails(); return; }
    const row = el("div", "ru-cloud-user");
    row.append(el("span", "", (state.user?.display_name || "Користувач") +
      (state.user?.role === "admin" ? " · Керівник" : " · Менеджер")));
    row.append(btn("Вийти", () => busy(row.lastElementChild, async () => {
      await request("logout");
      state.user = null;
      state.objects = [];
      state.selected = null;
      syncPins();
      renderSignedOut();
    })));
    panel.append(row);
    const controls = el("div", "ru-cloud-actions");
    controls.append(btn("↻ Оновити", () => busy(controls.firstElementChild, async () => {
      await loadObjects(); renderSignedIn(); notice("Дані синхронізовано.");
    })));
    controls.append(btn("+ Об'єкт", () => showCreateObject(), "is-primary"));
    const upload = btn("↑ Локальні точки", () => busy(upload, publishLocalPoints));
    controls.append(upload);
    const copyKey = btn("🔑 Мій ключ", () => busy(copyKey, async () => {
      await request("copyToken");
      notice("Ключ доступу скопійовано. Збережіть його у менеджері паролів; можна ввійти з іншого ПК.");
    }));
    controls.append(copyKey);
    if (state.user?.role === "admin") controls.append(btn("+ Менеджер", showCreateManager));
    panel.append(controls);
    const stats = el("div", "ru-cloud-summary", state.objects.length + " об'єктів доступно" +
      (state.user?.role === "admin" ? " · " + state.objects.filter(o => o.status === "pending").length + " очікують призначення" : ""));
    panel.append(stats);
    if (!state.objects.length) panel.append(el("p", "ru-cloud-muted", "Спільна база порожня. Додайте об’єкт або опублікуйте локальні GPS-точки кнопкою вище."));
    for (const obj of state.objects) {
      const card = btn((obj.status === "pending" ? "○ " : "● ") + safeText(obj.title) +
        (obj.status === "pending" ? " · без призначення" : ""), () => {
          state.selected = obj.id;
          renderSignedIn();
        }, "ru-cloud-item");
      panel.append(card);
    }
    panel.append(el("p", "ru-cloud-footnote", "Локальні точки публікуються тільки після підтвердження. Фото, ZIP та маршрути поки залишаються на комп’ютері."));
  }

  // Explicit, idempotent copy of local GeoZamir points into the shared database.
  // The original ZIP, photos and desktop project remain entirely local.
  async function stableUuid(input) {
    if (!window.crypto?.subtle) throw new Error("Не вдалося сформувати стабільний ідентифікатор об'єкта.");
    const digest = await window.crypto.subtle.digest("SHA-256", new TextEncoder().encode(input));
    const bytes = new Uint8Array(digest).slice(0, 16);
    bytes[6] = (bytes[6] & 15) | 64;
    bytes[8] = (bytes[8] & 63) | 128;
    const hex = Array.from(bytes, x => x.toString(16).padStart(2, "0")).join("");
    return hex.slice(0, 8) + "-" + hex.slice(8, 12) + "-" + hex.slice(12, 16) +
      "-" + hex.slice(16, 20) + "-" + hex.slice(20);
  }
  function localDay(record) {
    const fromProfile = window.RoundUpProfiles?.visitDate?.(record);
    const value = String(fromProfile || record.geo?.at || record.exportedAt ||
      record.images?.[0]?.date || record.date || "").slice(0, 10);
    return /^\\d{4}-\\d{2}-\\d{2}$/.test(value) ? value : "";
  }
  function localTitle(record) {
    const title = String(record.edit?.title || record.object || record.measurements?.[0]?.title ||
      record.sourceName || "Об'єкт з архіву").trim();
    return title.slice(0, 160).padEnd(2, " ");
  }
  async function publishLocalPoints() {
    const records = (bridge.state.records || []).filter(r => r && r.id);
    const pendingRecords = records.filter(r => Number.isFinite(Number(r.geo?.lat)) &&
      Number.isFinite(Number(r.geo?.lng)) && r.geo?.lat != null && r.geo?.lng != null);
    if (!pendingRecords.length) throw new Error("У поточному проєкті немає локальних точок із GPS.");
    if (!window.confirm("Передати локальні точки до спільної бази RoundUp?\\n\\n" +
        "Точок із геолокацією: " + pendingRecords.length +
        ".\\nБудуть передані координати, назви, дати та текстові примітки. " +
        "Самі фото й оригінальні ZIP залишаться на ПК.\\n" +
        "Менеджери побачать нові об'єкти після призначення керівником.")) return;
    const projectId = String(bridge.state.currentProjectId || "default").slice(0, 100);
    let created = 0, already = 0, failures = 0, attempted = 0;
    const known = new Set(state.objects.map(x => x.id));
    // Limit each click to 100 points so an interrupted upload can be retried safely.
    for (const record of pendingRecords) {
      const objectId = await stableUuid("roundup-object-v1|" + projectId + "|" + record.sourceKey + "|" + record.id);
      if (known.has(objectId)) { already++; continue; }
      if (attempted >= 100) break;
      attempted++;
      const day = localDay(record);
      const notes = [
        "Імпортовано з локального RoundUp",
        "Архів: " + String(record.sourceName || "невідомий"),
        day ? "" : "Дата об'їзду невідома; дата запису в базу: " + formDate(),
        record.meta?.comment ? "Коментар: " + String(record.meta.comment) : "",
        record.meta?.note && record.meta.note !== record.meta.comment ?
          "Примітка: " + String(record.meta.note) : "",
        record.edit?.notes ? "Офісна примітка: " + String(record.edit.notes) : "",
        record.measurements?.length ? "Замірів: " + record.measurements.length : "",
        record.images?.length ? "Фото залишились локально: " + record.images.length : ""
      ].filter(Boolean).join("\\n").slice(0, 3900);
      try {
        await api("POST", "/v1/objects", {
          id: objectId, projectId, title: localTitle(record),
          lat: Number(record.geo.lat), lon: Number(record.geo.lng)
        });
        const visitId = await stableUuid("roundup-visit-v1|" + objectId);
        await api("POST", "/v1/objects/" + objectId + "/visits", {
          id: visitId, visitDate: day || formDate(), note: notes
        });
        known.add(objectId);
        created++;
        notice("Передано " + created + " точок. Обробляємо " + attempted + " / " +
          Math.min(100, pendingRecords.length - already) + "...");
      } catch (error) {
        failures++;
        notice("Помилка публікації: " + safeText(error.message), true);
      }
    }
    await loadObjects();
    renderSignedIn();
    notice("Передано: " + created + " · вже у базі: " + already + " · помилок: " + failures +
      (records.length > attempted + already ? " · повторіть, щоб продовжити" : ""));
  }

  function showCreateObject() {
    panel.replaceChildren();
    heading("Новий спільний об'єкт");
    const title = field("Назва", "Наприклад, Гаражні ворота · Львів");
    const lat = field("Широта (необов'язково)", "49.8397", "number");
    const lon = field("Довгота (необов'язково)", "24.0297", "number");
    lat.input.step = "any"; lon.input.step = "any";
    panel.append(title.wrapper, lat.wrapper, lon.wrapper);
    const actions = el("div", "ru-cloud-actions");
    actions.append(btn("← Назад", renderSignedIn));
    const create = btn("Зберегти об'єкт", () => busy(create, async () => {
      const payload = { title: title.input.value.trim() };
      if (lat.input.value || lon.input.value) {
        payload.lat = lat.input.value ? Number(lat.input.value) : null;
        payload.lon = lon.input.value ? Number(lon.input.value) : null;
      }
      await api("POST", "/v1/objects", payload);
      await loadObjects(); renderSignedIn(); notice("Об'єкт створено. Керівник призначить відповідальних.");
    }), "is-primary");
    actions.append(create); panel.append(actions);
  }
  function showOneTimeToken(token, userName) {
    const dialog = el("div", "ru-cloud-token-dialog");
    dialog.append(el("strong", "", "Новий менеджер: " + userName));
    dialog.append(el("p", "", "Ключ видається один раз. Передайте його менеджеру приватним способом."));
    const value = el("textarea");
    value.value = token; value.readOnly = true; value.rows = 4;
    dialog.append(value);
    dialog.append(btn("Копіювати ключ", () => {
      value.select();
      if (navigator.clipboard?.writeText) void navigator.clipboard.writeText(token).catch(() => document.execCommand("copy"));
      else document.execCommand("copy");
    }));
    dialog.append(btn("Закрити", () => { dialog.remove(); renderSignedIn(); }));
    panel.append(dialog);
  }
  function showCreateManager() {
    panel.replaceChildren();
    heading("Новий менеджер");
    const name = field("Ім'я менеджера", "Наприклад, Олег Львів");
    panel.append(name.wrapper);
    const actions = el("div", "ru-cloud-actions");
    actions.append(btn("← Назад", renderSignedIn));
    const create = btn("Створити ключ", () => busy(create, async () => {
      const response = await api("POST", "/v1/users", { displayName: name.input.value.trim(), role: "manager" });
      state.users.push({ id: response.user.id, display_name: response.user.displayName, role: "manager", active: 1 });
      panel.replaceChildren();
      heading("Ключ менеджера");
      showOneTimeToken(response.apiToken, response.user.displayName);
    }), "is-primary");
    actions.append(create); panel.append(actions);
  }
  async function renderObjectDetails() {
    const id = state.selected;
    const obj = state.objects.find(o => o.id === id);
    if (!obj) { state.selected = null; renderSignedIn(); return; }
    const back = btn("← Усі об'єкти", () => { state.selected = null; renderSignedIn(); });
    panel.append(back);
    panel.append(el("h3", "ru-cloud-object-title", safeText(obj.title)));
    panel.append(el("p", "ru-cloud-muted", "Статус: " +
      (obj.status === "pending" ? "очікує призначення керівником" : "призначений") +
      (obj.lat != null ? " · " + Number(obj.lat).toFixed(5) + ", " + Number(obj.lon).toFixed(5) : "")));
    const body = el("div", "ru-cloud-detail");
    panel.append(body);
    try {
      const responses = await Promise.all([
        api("GET", "/v1/objects/" + id + "/assignments"),
        api("GET", "/v1/objects/" + id + "/visits"),
        api("GET", "/v1/objects/" + id + "/messages")
      ]);
      if (state.selected !== id) return;
      state.assignments = responses[0].assignments || [];
      state.visits = responses[1].visits || [];
      state.messages = responses[2].messages || [];
      paintDetails(body, obj);
    } catch (error) { notice(error.message, true); }
  }
  function paintDetails(body, obj) {
    body.replaceChildren();
    body.append(el("h4", "", "Відповідальні"));
    if (state.user?.role === "admin") {
      const assigned = new Set(state.assignments.map(a => a.id));
      const checkboxes = [];
      for (const manager of state.users) {
        const label = el("label", "ru-cloud-check");
        const check = el("input");
        check.type = "checkbox"; check.checked = assigned.has(manager.id);
        label.append(check, el("span", "", manager.display_name));
        checkboxes.push({ id: manager.id, check }); body.append(label);
      }
      if (!checkboxes.length) body.append(el("p", "ru-cloud-muted", "Створіть профіль менеджера, щоб призначити доступ."));
      const save = btn("Зберегти призначення", () => busy(save, async () => {
        await api("PUT", "/v1/objects/" + obj.id + "/assignments", {
          userIds: checkboxes.filter(x => x.check.checked).map(x => x.id)
        });
        await loadObjects(); renderSignedIn(); notice("Доступ менеджерів оновлено.");
      }), "is-primary");
      body.append(save);
    } else {
      body.append(el("p", "ru-cloud-muted",
        state.assignments.map(x => x.display_name).join(", ") || "Призначення очікується"));
    }
    body.append(el("h4", "", "Історія виїздів"));
    for (const visit of state.visits.slice(0, 30)) {
      body.append(el("div", "ru-cloud-visit", visit.visit_date + " · " + visit.manager_name + (visit.note ? "\n" + visit.note : "")));
    }
    const day = field("Дата виїзду", "", "date");
    day.input.value = formDate();
    const note = el("textarea");
    note.rows = 3; note.placeholder = "Коментар після виїзду або з офісу";
    const add = btn("+ Додати виїзд", () => busy(add, async () => {
      await api("POST", "/v1/objects/" + obj.id + "/visits", { visitDate: day.input.value, note: note.value });
      renderSignedIn(); notice("Виїзд додано.");
    }));
    body.append(day.wrapper, note, add);
    body.append(el("h4", "", "Чат по об'єкту"));
    const messages = el("div", "ru-cloud-messages");
    for (const message of state.messages) {
      const line = el("div", "ru-cloud-message");
      line.append(el("strong", "", message.sender_name + " · " + new Date(message.created_at).toLocaleString("uk-UA")));
      line.append(el("p", "", message.body));
      messages.append(line);
    }
    if (!state.messages.length) messages.append(el("p", "ru-cloud-muted", "Поки немає повідомлень."));
    body.append(messages);
    const reply = el("textarea");
    reply.rows = 3; reply.placeholder = "Написати відповідальному менеджеру...";
    body.append(reply);
    const send = btn("Надіслати повідомлення", () => busy(send, async () => {
      await api("POST", "/v1/objects/" + obj.id + "/messages", { body: reply.value });
      reply.value = ""; renderSignedIn(); notice("Повідомлення надіслано.");
    }), "is-primary");
    body.append(send);
  }
  function openPanel() {
    if (!state.opened) {
      state.opened = true;
      document.body.append(shade);
    }
    panel.replaceChildren();
    heading("Підключення...");
    void request("status").then(async response => {
      state.user = response.signedIn ? response.user : null;
      if (!state.user) { renderSignedOut(response.expired); return; }
      await loadObjects(); renderSignedIn();
    }).catch(error => { renderSignedOut(); notice(error.message, true); });
  }

  // Shared pins are an optional overlay: local map and existing projects are unchanged.
  const mapCanvas = document.getElementById("mapCanvas");
  const pins = el("div", "ru-cloud-pins");
  mapCanvas?.append(pins);
  function syncPins() {
    pins.replaceChildren();
    for (const obj of state.objects) {
      if (obj.lat == null || obj.lon == null) continue;
      const pin = btn("☁", () => {
        state.selected = obj.id;
        openPanel();
      }, "ru-cloud-pin");
      pin.title = obj.title + " · спільний об'єкт";
      pin.dataset.lat = String(obj.lat);
      pin.dataset.lon = String(obj.lon);
      pins.append(pin);
    }
    positionPins();
  }
  function positionPins() {
    if (!pins || !bridge.toScreen) return;
    pins.hidden = !!window.RoundUpProfiles?.isDrawing?.() || !!window.RoundUpCoverage?.isDrawing?.();
    const width = mapCanvas?.clientWidth || 0;
    const height = mapCanvas?.clientHeight || 0;
    for (const pin of pins.children) {
      const screen = bridge.toScreen(Number(pin.dataset.lat), Number(pin.dataset.lon));
      const visible = screen && Number.isFinite(screen.x) && Number.isFinite(screen.y) &&
        screen.x >= 0 && screen.y >= 0 && screen.x <= width && screen.y <= height;
      pin.hidden = !visible;
      if (visible) { pin.style.left = screen.x + "px"; pin.style.top = screen.y + "px"; }
    }
  }
  setInterval(positionPins, 550);

  // On native startup, automatically show all server-authorized cloud objects.
  // Signed-out local installations remain purely offline without prompts.
  if (native) {
    void request("status").then(async response => {
      if (response.signedIn) {
        state.user = response.user;
        await loadObjects();
      }
    }).catch(() => { /* an unavailable cloud never blocks original RoundUp startup */ });
  }

  // Refresh authorized cloud objects and revocations while the app stays open.
  // Local ZIP/route renderers are not affected by network outages.
  let refreshingObjects = false;
  setInterval(async () => {
    if (!state.user || refreshingObjects) return;
    refreshingObjects = true;
    try { await loadObjects(); }
    catch (_) { /* keep the last known map while offline */ }
    finally { refreshingObjects = false; }
  }, 60000);

  // Poll notifications only after login; native Windows notifications can be added separately.
  setInterval(async () => {
    if (!state.user || state.polling) return;
    state.polling = true;
    try {
      let path = "/v1/notifications?after=" + encodeURIComponent(state.notificationAfter);
      if (state.notificationAfterId) path += "&afterId=" + encodeURIComponent(state.notificationAfterId);
      const response = await api("GET", path);
      state.notificationAfter = response.nextAfter || state.notificationAfter;
      state.notificationAfterId = response.nextAfterId || state.notificationAfterId;
      if (response.notifications?.length) {
        state.notifyCount += response.notifications.length;
        trigger.textContent = "☁ Спільне середовище (" + state.notifyCount + ")";
        notice("Нові повідомлення: " + response.notifications.length);
      }
    } catch (_) { /* connection can be offline; local interpreter remains usable */ }
    finally { state.polling = false; }
  }, 30000);
})();
