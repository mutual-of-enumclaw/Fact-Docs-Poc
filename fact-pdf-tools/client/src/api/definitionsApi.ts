const API_BASE = 'http://localhost:5035/api';

export interface FormDefinitionField {
  name: string;
  page: number;
  x: number;
  y: number;
  width: number;
  height: number;
  fontId: number;
  pointSize: number;
  bold: boolean;
  maxLength: number;
}

export interface FormDefinitionStaticText {
  text: string;
  page: number;
  x: number;
  y: number;
  width: number;
  height: number;
  fontId: number;
  pointSize: number;
  bold: boolean;
}

export interface FormDefinitionLine {
  page: number;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  lineWidth: number;
  style: number;
}

export interface FormDefinition {
  id: string;
  name: string;
  description: string;
  sourceFormNumber?: string;
  sourceEditionDate?: string;
  pageCount: number;
  pageWidth: number;   // PDF points (letter = 612)
  pageHeight: number;  // PDF points (letter = 792)
  fields: FormDefinitionField[];
  staticTexts: FormDefinitionStaticText[];
  lines: FormDefinitionLine[];
  createdAt: string;
  updatedAt: string;
}

export interface FormDefinitionSummary {
  id: string;
  name: string;
  description: string;
  sourceFormNumber?: string;
  sourceEditionDate?: string;
  pageCount: number;
  pageWidth: number;
  pageHeight: number;
  fieldCount: number;
  createdAt: string;
  updatedAt: string;
}

export async function fetchDefinitions(): Promise<FormDefinitionSummary[]> {
  const res = await fetch(`${API_BASE}/definitions`);
  if (!res.ok) throw new Error(`Failed to fetch definitions: ${res.statusText}`);
  return res.json();
}

export async function fetchDefinition(id: string): Promise<FormDefinition> {
  const res = await fetch(`${API_BASE}/definitions/${encodeURIComponent(id)}`);
  if (!res.ok) throw new Error(`Failed to fetch definition: ${res.statusText}`);
  return res.json();
}

export async function saveDefinition(def: FormDefinition): Promise<FormDefinition> {
  const res = await fetch(`${API_BASE}/definitions/${encodeURIComponent(def.id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(def),
  });
  if (!res.ok) throw new Error(`Failed to save definition: ${res.statusText}`);
  return res.json();
}

export async function deleteDefinition(id: string): Promise<void> {
  const res = await fetch(`${API_BASE}/definitions/${encodeURIComponent(id)}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(`Failed to delete definition: ${res.statusText}`);
}

export async function renderDefinition(
  id: string,
  fieldValues?: Record<string, string>,
  flatten = true,
): Promise<Blob> {
  const res = await fetch(`${API_BASE}/definitions/${encodeURIComponent(id)}/render`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ fieldValues: fieldValues ?? {}, flatten }),
  });
  if (!res.ok) throw new Error(`Render failed: ${res.statusText}`);
  return res.blob();
}

export async function importLegacy(
  formNumber: string,
  editionDate: string,
  name?: string,
): Promise<FormDefinition> {
  const res = await fetch(`${API_BASE}/definitions/import-legacy`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ formNumber, editionDate, name }),
  });
  if (!res.ok) throw new Error(`Import failed: ${res.statusText}`);
  return res.json();
}

export async function importFromPdf(file: File, name?: string): Promise<FormDefinition> {
  const body = new FormData();
  body.append('file', file);
  if (name) body.append('name', name);
  const res = await fetch(`${API_BASE}/definitions/import-pdf`, { method: 'POST', body });
  if (!res.ok) {
    const text = await res.text().catch(() => res.statusText);
    throw new Error(`PDF import failed: ${text}`);
  }
  return res.json();
}
