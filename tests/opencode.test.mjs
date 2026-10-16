import assert from "node:assert/strict";
import { test } from "node:test";
import { metadata, Monitor } from "../integrations/opencode/monitor.js";

const directory = "D:\\projects\\example";
function event(type, data = {}, sessionID = "ses_test") {
  return metadata({ type, location: { directory }, data: { sessionID, ...data } });
}
function apply(monitor, type, data, sessionID) {
  return monitor.apply(event(type, data, sessionID), directory).map(value => value.hook_event_name);
}

test("parallel tools retain execution and all pending approvals take priority", () => {
  const monitor = new Monitor();
  assert.deepEqual(apply(monitor, "session.execution.started"), ["UserPromptSubmit"]);
  apply(monitor, "session.tool.called", { id: "tool-a" });
  apply(monitor, "session.tool.called", { id: "tool-b" });
  assert.deepEqual(apply(monitor, "permission.asked", { id: "permission-a" }), ["PermissionRequest"]);
  apply(monitor, "permission.asked", { id: "permission-b" });
  assert.deepEqual(apply(monitor, "session.tool.success", { id: "tool-a" }), ["PermissionRequest"]);
  assert.deepEqual(apply(monitor, "permission.replied", { requestID: "permission-a" }), ["PermissionRequest"]);
  assert.deepEqual(apply(monitor, "permission.replied", { requestID: "permission-b" }), ["PreToolUse"]);
  assert.deepEqual(apply(monitor, "session.tool.failed", { id: "tool-b" }), ["Thinking"]);
  assert.deepEqual(apply(monitor, "session.execution.succeeded"), ["Thinking", "Stop"]);
});

test("failure and interruption cannot become successful completion on idle", () => {
  const monitor = new Monitor();
  apply(monitor, "session.execution.started");
  assert.deepEqual(apply(monitor, "session.execution.failed"), ["Thinking", "StopFailure"]);
  assert.deepEqual(apply(monitor, "session.status", { status: { type: "idle" } }), []);
  apply(monitor, "session.execution.started");
  assert.deepEqual(apply(monitor, "session.execution.interrupted"), ["Thinking", "Interrupt"]);
  assert.deepEqual(apply(monitor, "session.status", { status: { type: "idle" } }), []);
});

test("existing sessions and child sessions work without a creation event", () => {
  const monitor = new Monitor();
  assert.deepEqual(apply(monitor, "session.step.started"), ["Thinking"]);
  apply(monitor, "session.execution.started", {}, "ses_child");
  assert.deepEqual(apply(monitor, "session.execution.succeeded", {}, "ses_child"), ["Thinking", "Stop"]);
  assert.equal(monitor.sessions.get("ses_test").running, true);
  assert.deepEqual(apply(monitor, "session.deleted", {}, "ses_child"), ["SessionEnd"]);
  assert.equal(monitor.sessions.has("ses_child"), false);
  assert.deepEqual(monitor.close(), [{ session_id: "ses_test", hook_event_name: "SessionEnd", cwd: directory }]);
});

test("metadata projection excludes user input, tool content and error details", () => {
  const value = event("session.tool.called", { id: "tool-a", input: { secret: "DO_NOT_STORE" }, content: "DO_NOT_STORE", error: "DO_NOT_STORE" });
  assert.equal(JSON.stringify(value).includes("DO_NOT_STORE"), false);
  assert.equal(event("session.text.delta", { delta: "DO_NOT_STORE" }), undefined);
  assert.equal(event("session.execution.started", {}, "x".repeat(201)), undefined);
});

test("plugin loading mid-task still supplies a completion state", () => {
  assert.deepEqual(apply(new Monitor(), "session.execution.failed"), ["Thinking", "StopFailure"]);
});
