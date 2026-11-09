// Єдиний безпечний хелпер для формування текстових вузлів у DOM
function createSafeTextElement(tagName, text) {
  const element = document.createElement(tagName);
  element.textContent = text ?? "";
  return element;
}

let current;
const id = () => encodeURIComponent(document.querySelector("#incident-id").value.trim());
const status = document.querySelector("#status");

async function load() {
  const response = await fetch(`/api/incidents/${id()}`);
  status.textContent = `GET: ${response.status}`;
  if (!response.ok) return;
  current = await response.json();
  document.querySelector("#title").textContent = current.title;

  // Безпечне відображення опису через хелпер замість innerHTML
  const descriptionContainer = document.querySelector("#description");
  descriptionContainer.replaceChildren(createSafeTextElement("p", current.description));

  // Безпечне відображення коментарів через той самий хелпер
  const commentsContainer = document.querySelector("#comments");
  commentsContainer.replaceChildren();
  for (const comment of current.comments ?? []) {
    commentsContainer.append(createSafeTextElement("p", comment.text));
  }

  document.querySelector("#edit").elements.description.value = current.description;
}

document.querySelector("#load").addEventListener("click", load);
document.querySelector("#edit").addEventListener("submit", async event => {
  event.preventDefault();
  const response = await fetch("/api/incidents", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      title: event.currentTarget.elements.title.value,
      description: event.currentTarget.elements.description.value,
      severity: "Low",
      occurredAtUtc: new Date().toISOString()
    })
  });
  status.textContent = `POST: ${response.status}`;
  if (response.ok) {
    const created = await response.json();
    document.querySelector("#incident-id").value = created.id;
    await load();
  }
});