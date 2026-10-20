"use strict";

const bridge = window.external;
const buttons = document.querySelectorAll("button[data-command]");
let activationAcknowledged = false;

for (const button of buttons) {
  button.disabled = true;
  button.addEventListener("click", () => {
    bridge.sendMessage(button.dataset.command);
  });
}

bridge.receiveMessage(message => {
  const state = JSON.parse(message);
  document.getElementById("status").textContent = state.status;
  document.getElementById("process").textContent =
    `Process ${state.processId} · ${state.launch} · Notifications: ${state.setting}`;
  document.getElementById("action").textContent = state.lastAction || "No callback yet";
  document.getElementById("reply").textContent = state.lastReply || "—";
  document.getElementById("choice").textContent = state.lastChoice || "—";
  document.getElementById("history").textContent = state.history;
  document.getElementById("events").textContent = state.events;

  for (const button of buttons) {
    button.disabled = false;
  }

  // The integration probe waits for this acknowledgment, which is sent only
  // after the exact COM-delivered values have been rendered into the page.
  if (!activationAcknowledged && state.lastAction === "reply" &&
      state.lastReply === "Hello & + 世界 😀" && state.lastChoice === "two") {
    activationAcknowledged = true;
    bridge.sendMessage("activation-rendered");
  }
});

bridge.sendMessage("ready");
