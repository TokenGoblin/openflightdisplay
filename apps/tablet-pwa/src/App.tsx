import { useState } from "react";
import { SetupWizard } from "./components/SetupWizard";
import { DisplayPage } from "./pages/DisplayPage";
import { loadStoredConnection, type StoredConnection } from "./lib/storage";

export function App() {
  const [connection, setConnection] = useState<StoredConnection | null>(() => loadStoredConnection());

  if (!connection) {
    return <SetupWizard onComplete={setConnection} />;
  }

  return <DisplayPage connection={connection} onReconfigure={() => setConnection(null)} />;
}
