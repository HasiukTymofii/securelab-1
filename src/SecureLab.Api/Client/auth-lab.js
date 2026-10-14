const result = document.querySelector("#result");
const stateEl = document.querySelector("#state");
const profileBox = document.querySelector("#profile");
const profileEmpty = document.querySelector("#profile-empty");

function say(text) {
  result.textContent = text;
}

function showState(kind) {
  const labels = {
    anonymous: "анонім (не ввійшли)",
    "signed-in": "сеанс активний",
    unknown: "невідомо (помилка перевірки)"
  };
  stateEl.textContent = labels[kind];
}

function clearProfile() {
  document.querySelector("#profile-id").textContent = "";
  document.querySelector("#profile-userName").textContent = "";
  document.querySelector("#profile-displayName").textContent = "";
  profileBox.hidden = true;
  profileEmpty.hidden = false;
}

function renderProfile(data) {
  document.querySelector("#profile-id").textContent = String(data.id ?? "");
  document.querySelector("#profile-userName").textContent = String(data.userName ?? "");
  document.querySelector("#profile-displayName").textContent = String(data.displayName ?? "");
  profileBox.hidden = false;
  profileEmpty.hidden = true;
}

async function apiFetch(path, options = {}) {
  return fetch(path, { ...options, credentials: "same-origin" });
}

function postJson(path, body) {
  return apiFetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
}

async function refreshSessionState() {
  try {
    const response = await apiFetch("/api/auth/session-check");
    if (response.status === 204) showState("signed-in");
    else if (response.status === 401) showState("anonymous");
    else showState("unknown");
  } catch {
    showState("unknown");
  }
}

// Повертає HTTP status; профіль показує лише для 200 з JSON.
async function loadProfile() {
  try {
    const response = await apiFetch("/api/me");
    const type = response.headers.get("Content-Type") ?? "";
    if (response.status === 401) {
      clearProfile();
      showState("anonymous");
    } else if (response.ok && type.includes("application/json")) {
      renderProfile(await response.json());
      showState("signed-in");
    } else {
      clearProfile();
      showState("unknown");
    }
    return response.status;
  } catch {
    clearProfile();
    showState("unknown");
    return 0;
  }
}

document.querySelector("#register").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const data = new FormData(form);
  const response = await postJson("/api/auth/register", {
    userName: data.get("userName"),
    email: data.get("email"),
    displayName: data.get("displayName"),
    password: data.get("password")
  });
  form.elements.password.value = "";
  if (response.status === 201) {
    say("Register: 201. Тепер увійдіть цим користувачем.");
  } else {
    say(`Register: ${response.status}`);
  }
});

document.querySelector("#login").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const data = new FormData(form);
  const response = await postJson("/api/auth/login", {
    userName: data.get("userName"),
    password: data.get("password")
  });
  form.elements.password.value = "";
  say(`Login: ${response.status}`);
  if (response.status === 204) {
    await loadProfile();
  } else {
    clearProfile();
    await refreshSessionState();
  }
});

document.querySelector("#me").addEventListener("click", async () => {
  const status = await loadProfile();
  say(`/api/me: ${status}`);
});

document.querySelector("#incident").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const data = new FormData(form);
  // Без owner, role, status, id: ними керує сервер.
  const response = await postJson("/api/incidents", {
    title: data.get("title"),
    description: data.get("description"),
    severity: data.get("severity"),
    occurredAtUtc: new Date(data.get("occurredAt")).toISOString()
  });
  if (response.status === 201) {
    const created = await response.json();
    say(`Create: 201, id=${created.id}, title=${created.title}`);
    form.elements.title.value = "";
    form.elements.description.value = "";
  } else if (response.status === 401) {
    clearProfile();
    showState("anonymous");
    say("Create: 401 (потрібен вхід)");
  } else {
    say(`Create: ${response.status}`);
  }
});

document.querySelector("#logout").addEventListener("click", async () => {
  const response = await apiFetch("/api/auth/logout", { method: "POST" });
  clearProfile();
  document.querySelector("#incident").reset();
  setDefaultTime();
  const meStatus = await loadProfile();
  say(`Logout: ${response.status}; /api/me після виходу: ${meStatus}`);
});

function setDefaultTime() {
  const d = new Date(Date.now() - 10 * 60 * 1000);
  const pad = n => String(n).padStart(2, "0");
  document.querySelector("#incident").elements.occurredAt.value =
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

setDefaultTime();
clearProfile();
refreshSessionState();