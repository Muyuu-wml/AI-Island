// OpenCode V2 public event contract (tested against 2.0.26).
// Retain metadata only; never enqueue prompts, tool arguments or results.
const kinds = new Set([
  "session.created", "session.deleted", "session.moved",
  "session.execution.started", "session.execution.succeeded", "session.execution.failed", "session.execution.interrupted",
  "session.step.started", "session.retry.scheduled", "session.tool.called", "session.tool.success", "session.tool.failed",
  "permission.asked", "permission.replied", "session.status",
]);

export function metadata(event) {
  if (!kinds.has(event.type)) return;
  const data = event.data;
  if (typeof data?.sessionID !== "string" || !data.sessionID || data.sessionID.length > 200) return;
  return {
    type: event.type, sessionID: data.sessionID,
    directory: event.location?.directory,
    destination: data.location?.directory,
    id: typeof data.id === "string" ? data.id.slice(0, 200) : undefined,
    requestID: typeof data.requestID === "string" ? data.requestID.slice(0, 200) : undefined,
    status: data.status?.type,
  };
}

export class Monitor {
  sessions = new Map();

  apply(event, directory) {
    let session = this.sessions.get(event.sessionID);
    if (!session) {
      if (this.sessions.size >= 256) {
        const idle = [...this.sessions].find(([, value]) => !value.running);
        if (!idle) return [];
        this.sessions.delete(idle[0]);
      }
      session = { running: false, tools: new Set(), permissions: new Set(), directory };
      this.sessions.set(event.sessionID, session);
    }
    session.directory = directory;
    const result = [];
    const emit = (kind) => result.push({ session_id: event.sessionID, hook_event_name: kind, cwd: directory });
    const reset = () => { session.tools.clear(); session.permissions.clear(); };
    switch (event.type) {
      case "session.created":
        if (!session.running) emit("SessionStart");
        return result;
      case "session.deleted":
      case "session.moved":
        this.sessions.delete(event.sessionID);
        emit("SessionEnd");
        return result;
      case "session.execution.started":
        reset(); session.running = true; emit("UserPromptSubmit");
        return result;
      case "session.execution.succeeded":
      case "session.execution.failed":
      case "session.execution.interrupted":
        // Island may have restarted or resumed monitoring halfway through this
        // execution. Establish an active state before its terminal event.
        emit("Thinking");
        reset(); session.running = false;
        emit(event.type.endsWith("succeeded") ? "Stop" : event.type.endsWith("failed") ? "StopFailure" : "Interrupt");
        return result;
      case "session.tool.called":
        if (event.id) session.tools.add(event.id);
        break;
      case "session.tool.success":
      case "session.tool.failed":
        if (event.id) session.tools.delete(event.id);
        break;
      case "permission.asked":
        if (event.id) session.permissions.add(event.id);
        break;
      case "permission.replied":
        if (event.requestID) session.permissions.delete(event.requestID);
        break;
      case "session.status":
        // Idle is not proof of success: use the execution outcome events instead.
        if (event.status !== "busy" && event.status !== "retry") return [];
        break;
    }
    session.running = true;
    // Aggregate parallel tools/permissions before sending a state. One completed
    // tool must not hide another outstanding permission prompt.
    emit(session.permissions.size ? "PermissionRequest" : session.tools.size ? "PreToolUse" : "Thinking");
    return result;
  }

  close() {
    const events = [...this.sessions].map(([id, session]) => ({ session_id: id, hook_event_name: "SessionEnd", cwd: session.directory }));
    this.sessions.clear();
    return events;
  }
}
