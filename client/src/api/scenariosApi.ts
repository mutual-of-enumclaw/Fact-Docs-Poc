const API_BASE = 'http://localhost:5035/api';

export interface FormScenario {
  id: string;
  name: string;
  formNumber: string;
  editionDate: string;
  fieldValues: Record<string, string>;
  createdAt: string;
  updatedAt: string;
}

export async function fetchScenarios(): Promise<FormScenario[]> {
  const res = await fetch(`${API_BASE}/scenarios`);
  if (!res.ok) throw new Error(`Failed to fetch scenarios: ${res.statusText}`);
  return res.json();
}

export async function fetchScenario(id: string): Promise<FormScenario> {
  const res = await fetch(`${API_BASE}/scenarios/${encodeURIComponent(id)}`);
  if (!res.ok) throw new Error(`Failed to fetch scenario: ${res.statusText}`);
  return res.json();
}

export async function createScenario(
  scenario: Omit<FormScenario, 'id' | 'createdAt' | 'updatedAt'>,
): Promise<FormScenario> {
  const res = await fetch(`${API_BASE}/scenarios`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(scenario),
  });
  if (!res.ok) throw new Error(`Failed to create scenario: ${res.statusText}`);
  return res.json();
}

export async function updateScenario(scenario: FormScenario): Promise<FormScenario> {
  const res = await fetch(`${API_BASE}/scenarios/${encodeURIComponent(scenario.id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(scenario),
  });
  if (!res.ok) throw new Error(`Failed to update scenario: ${res.statusText}`);
  return res.json();
}

export async function deleteScenario(id: string): Promise<void> {
  const res = await fetch(`${API_BASE}/scenarios/${encodeURIComponent(id)}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(`Failed to delete scenario: ${res.statusText}`);
}

export async function runScenario(id: string): Promise<Blob> {
  const res = await fetch(`${API_BASE}/scenarios/${encodeURIComponent(id)}/run`, { method: 'POST' });
  if (!res.ok) throw new Error(`Failed to run scenario: ${res.statusText}`);
  return res.blob();
}
