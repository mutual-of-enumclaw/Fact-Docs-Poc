import { useState, useRef, useCallback, useEffect } from 'react';
import {
  Layout, Button, Space, Typography, Input, InputNumber, Form, Drawer, Modal,
  List, Tag, Divider, Spin, message, Tooltip, Empty, Select,
} from 'antd';
import {
  PlusOutlined, EditOutlined, DeleteOutlined, EyeOutlined, ImportOutlined,
  FontSizeOutlined, LineOutlined, SaveOutlined, FileAddOutlined, UploadOutlined,
  LeftOutlined, RightOutlined,
} from '@ant-design/icons';
import { useSearchParams } from 'react-router-dom';
import { Worker, Viewer, SpecialZoomLevel } from '@react-pdf-viewer/core';
import { defaultLayoutPlugin } from '@react-pdf-viewer/default-layout';
import '@react-pdf-viewer/core/lib/styles/index.css';
import '@react-pdf-viewer/default-layout/lib/styles/index.css';
import {
  fetchDefinitions, fetchDefinition, saveDefinition, deleteDefinition,
  renderDefinition, importLegacy, importFromPdf,
  type FormDefinition, type FormDefinitionField,
  type FormDefinitionStaticText, type FormDefinitionLine,
  type FormDefinitionSummary,
} from '../api/definitionsApi';

const { Sider, Content } = Layout;
const { Title, Text } = Typography;

type ElementType = 'field' | 'text' | 'line';
type AnyElement = { kind: 'field'; data: FormDefinitionField }
  | { kind: 'text'; data: FormDefinitionStaticText }
  | { kind: 'line'; data: FormDefinitionLine };

interface DragState {
  type: ElementType;
  index: number;
  startMouseX: number;
  startMouseY: number;
  startElemX: number;
  startElemY: number;
}

const CANVAS_SCALE = 1; // 1 CSS px = 1 PDF pt

function newField(page: number): FormDefinitionField {
  return { name: `Field${Date.now()}`, page, x: 100, y: 100, width: 144, height: 14, fontId: 0, pointSize: 10, bold: false, maxLength: 50 };
}

function newText(page: number): FormDefinitionStaticText {
  return { text: 'Label', page, x: 100, y: 80, width: 80, height: 14, fontId: 0, pointSize: 10, bold: false };
}

function newLine(page: number): FormDefinitionLine {
  return { page, x1: 60, y1: 200, x2: 552, y2: 200, lineWidth: 1, style: 0 };
}

function emptyDefinition(): FormDefinition {
  return {
    id: Math.random().toString(36).slice(2, 10),
    name: 'New Form',
    description: '',
    pageCount: 1,
    pageWidth: 612,
    pageHeight: 792,
    fields: [],
    staticTexts: [],
    lines: [],
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  };
}

export default function DesignPage() {
  const [searchParams] = useSearchParams();
  const [definitions, setDefinitions] = useState<FormDefinitionSummary[]>([]);
  const [defsLoading, setDefsLoading] = useState(false);
  const [def, setDef] = useState<FormDefinition | null>(null);
  const [saving, setSaving] = useState(false);
  const [pdfUrl, setPdfUrl] = useState<string | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [showPreview, setShowPreview] = useState(false);
  const [selected, setSelected] = useState<{ type: ElementType; index: number } | null>(null);
  const [importOpen, setImportOpen] = useState(false);
  const [importing, setImporting] = useState(false);
  const [importForm, setImportForm] = useState({ formNumber: '', editionDate: '', name: '' });
  const [currentPage, setCurrentPage] = useState(1);
  const [pageImageUrl, setPageImageUrl] = useState<string | null>(null);
  const dragRef = useRef<DragState | null>(null);
  const canvasRef = useRef<HTMLDivElement>(null);
  const pdfUploadRef = useRef<HTMLInputElement>(null);
  const defaultLayoutPluginInstance = defaultLayoutPlugin();

  const loadDefinitions = useCallback(async () => {
    setDefsLoading(true);
    try {
      setDefinitions(await fetchDefinitions());
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : 'Failed to load forms');
    } finally {
      setDefsLoading(false);
    }
  }, []);

  useEffect(() => { loadDefinitions(); }, [loadDefinitions]);

  // Auto-open a specific definition when navigated from another page (e.g. Convert)
  useEffect(() => {
    const id = searchParams.get('id');
    if (id) openDefinition(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Reset to page 1 whenever a different definition is opened
  useEffect(() => { setCurrentPage(1); setSelected(null); }, [def?.id]);

  // Load PDF page background image
  useEffect(() => {
    if (!def?.id) { setPageImageUrl(null); return; }
    const url = `${import.meta.env.VITE_API_BASE ?? 'http://localhost:5035/api'}/definitions/${def.id}/page-image?page=${currentPage}&dpi=144`;
    setPageImageUrl(url);
  }, [def?.id, currentPage]);

  useEffect(() => () => { if (pdfUrl) URL.revokeObjectURL(pdfUrl); }, [pdfUrl]);

  const openDefinition = async (id: string) => {
    try {
      setDef(await fetchDefinition(id));
      setSelected(null);
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : 'Failed to load form');
    }
  };

  const handleNew = () => { setDef(emptyDefinition()); setSelected(null); };

  const handleSave = async () => {
    if (!def) return;
    setSaving(true);
    try {
      const saved = await saveDefinition(def);
      setDef(saved);
      await loadDefinitions();
      message.success('Saved');
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (id: string) => {
    try {
      await deleteDefinition(id);
      if (def?.id === id) setDef(null);
      await loadDefinitions();
      message.success('Deleted');
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : 'Delete failed');
    }
  };

  const handlePreview = async () => {
    if (!def) return;
    setPreviewing(true);
    try {
      const saved = await saveDefinition(def);
      setDef(saved);
      const blob = await renderDefinition(saved.id, {}, false);
      if (pdfUrl) URL.revokeObjectURL(pdfUrl);
      setPdfUrl(URL.createObjectURL(blob));
      setShowPreview(true);
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : 'Preview failed');
    } finally {
      setPreviewing(false);
    }
  };

  const handleImport = async () => {
    if (!importForm.formNumber || !importForm.editionDate) {
      message.warning('Enter form number and edition date');
      return;
    }
    setImporting(true);
    try {
      const imported = await importLegacy(
        importForm.formNumber.toUpperCase(),
        importForm.editionDate,
        importForm.name || undefined,
      );
      setDef(imported);
      setSelected(null);
      await loadDefinitions();
      setImportOpen(false);
      message.success(`Imported ${imported.fields.length} fields, ${imported.staticTexts.length} texts, ${imported.lines.length} lines`);
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : 'Import failed');
    } finally {
      setImporting(false);
    }
  };

  const handlePdfUpload = async (file: File) => {
    setImporting(true);
    try {
      const imported = await importFromPdf(file, file.name.replace(/\.pdf$/i, ''));
      setDef(imported);
      setSelected(null);
      await loadDefinitions();
      message.success(`Imported PDF: ${imported.staticTexts.length} text blocks, ${imported.fields.length} form fields`);
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : 'PDF import failed');
    } finally {
      setImporting(false);
    }
  };

  // ---- Canvas interaction ----

  const updateDef = (updater: (d: FormDefinition) => FormDefinition) => {
    setDef((prev) => prev ? updater(prev) : prev);
  };

  const getCanvasOffset = () => {
    const rect = canvasRef.current?.getBoundingClientRect();
    return rect ? { left: rect.left, top: rect.top } : { left: 0, top: 0 };
  };

  const onMouseDown = (e: React.MouseEvent, type: ElementType, index: number) => {
    e.stopPropagation();
    setSelected({ type, index });
    if (!def) return;
    let startX = 0, startY = 0;
    if (type === 'field') { startX = def.fields[index].x; startY = def.fields[index].y; }
    else if (type === 'text') { startX = def.staticTexts[index].x; startY = def.staticTexts[index].y; }
    else { startX = def.lines[index].x1; startY = def.lines[index].y1; }
    dragRef.current = { type, index, startMouseX: e.clientX, startMouseY: e.clientY, startElemX: startX, startElemY: startY };
  };

  const onCanvasMouseMove = (e: React.MouseEvent) => {
    if (!dragRef.current || !def) return;
    const { type, index, startMouseX, startMouseY, startElemX, startElemY } = dragRef.current;
    const dx = (e.clientX - startMouseX) / CANVAS_SCALE;
    const dy = (e.clientY - startMouseY) / CANVAS_SCALE;
    const nx = Math.max(0, startElemX + dx);
    const ny = Math.max(0, startElemY + dy);
    updateDef((d) => {
      if (type === 'field') {
        const fields = [...d.fields];
        fields[index] = { ...fields[index], x: nx, y: ny };
        return { ...d, fields };
      } else if (type === 'text') {
        const texts = [...d.staticTexts];
        texts[index] = { ...texts[index], x: nx, y: ny };
        return { ...d, staticTexts: texts };
      } else {
        const lines = [...d.lines];
        const diffX = nx - lines[index].x1;
        const diffY = ny - lines[index].y1;
        lines[index] = { ...lines[index], x1: nx, y1: ny, x2: lines[index].x2 + diffX, y2: lines[index].y2 + diffY };
        return { ...d, lines };
      }
    });
  };

  const onCanvasMouseUp = () => { dragRef.current = null; };

  const addField = () => {
    if (!def) return;
    const page = def.pageCount;
    updateDef((d) => ({ ...d, fields: [...d.fields, newField(page)] }));
    setSelected({ type: 'field', index: def.fields.length });
  };

  const addText = () => {
    if (!def) return;
    const page = def.pageCount;
    updateDef((d) => ({ ...d, staticTexts: [...d.staticTexts, newText(page)] }));
    setSelected({ type: 'text', index: def.staticTexts.length });
  };

  const addLine = () => {
    if (!def) return;
    const page = def.pageCount;
    updateDef((d) => ({ ...d, lines: [...d.lines, newLine(page)] }));
    setSelected({ type: 'line', index: def.lines.length });
  };

  const deleteSelected = () => {
    if (!selected || !def) return;
    updateDef((d) => {
      if (selected.type === 'field') {
        const fields = [...d.fields]; fields.splice(selected.index, 1); return { ...d, fields };
      } else if (selected.type === 'text') {
        const texts = [...d.staticTexts]; texts.splice(selected.index, 1); return { ...d, staticTexts: texts };
      } else {
        const lines = [...d.lines]; lines.splice(selected.index, 1); return { ...d, lines };
      }
    });
    setSelected(null);
  };

  // ---- Selected-element helpers ----

  const selectedElement: AnyElement | null = selected && def
    ? selected.type === 'field'
      ? { kind: 'field', data: def.fields[selected.index] }
      : selected.type === 'text'
      ? { kind: 'text', data: def.staticTexts[selected.index] }
      : { kind: 'line', data: def.lines[selected.index] }
    : null;

  const updateField = (upd: Partial<FormDefinitionField>) => {
    if (!selected || selected.type !== 'field') return;
    updateDef((d) => {
      const fields = [...d.fields]; fields[selected.index] = { ...fields[selected.index], ...upd };
      return { ...d, fields };
    });
  };

  const updateText = (upd: Partial<FormDefinitionStaticText>) => {
    if (!selected || selected.type !== 'text') return;
    updateDef((d) => {
      const texts = [...d.staticTexts]; texts[selected.index] = { ...texts[selected.index], ...upd };
      return { ...d, staticTexts: texts };
    });
  };

  const updateLine = (upd: Partial<FormDefinitionLine>) => {
    if (!selected || selected.type !== 'line') return;
    updateDef((d) => {
      const lines = [...d.lines]; lines[selected.index] = { ...lines[selected.index], ...upd };
      return { ...d, lines };
    });
  };

  const canvasW = (def?.pageWidth ?? 612) * CANVAS_SCALE;
  const canvasH = (def?.pageHeight ?? 792) * CANVAS_SCALE;

  return (
    <div style={{ display: 'flex', gap: 0, height: 'calc(100vh - 112px)' }}>
      {/* Left sidebar: form list */}
      <div style={{ width: 220, borderRight: '1px solid #f0f0f0', overflow: 'auto', padding: '0 8px 8px' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', margin: '8px 0' }}>
          <Text strong>Authored Forms</Text>
          <Space size={4}>
            <Tooltip title="New blank form"><Button size="small" icon={<FileAddOutlined />} onClick={handleNew} /></Tooltip>
            <Tooltip title="Import legacy FAP"><Button size="small" icon={<ImportOutlined />} onClick={() => setImportOpen(true)} /></Tooltip>
            <Tooltip title="Import existing PDF"><Button size="small" icon={<UploadOutlined />} loading={importing} onClick={() => pdfUploadRef.current?.click()} /></Tooltip>
          </Space>
        </div>
        <input
          ref={pdfUploadRef}
          type="file"
          accept=".pdf,application/pdf"
          style={{ display: 'none' }}
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) handlePdfUpload(file);
            e.target.value = '';
          }}
        />
        {defsLoading ? <Spin size="small" /> : definitions.length === 0
          ? <Empty description="No authored forms yet" image={Empty.PRESENTED_IMAGE_SIMPLE} style={{ marginTop: 24 }} />
          : <List
              size="small"
              dataSource={definitions}
              renderItem={(d) => (
                <List.Item
                  style={{ cursor: 'pointer', padding: '4px 0', background: def?.id === d.id ? '#e6f4ff' : undefined, borderRadius: 4 }}
                  actions={[
                    <Button key="del" type="text" size="small" danger icon={<DeleteOutlined />}
                      onClick={(e) => { e.stopPropagation(); handleDelete(d.id); }} />,
                  ]}
                  onClick={() => openDefinition(d.id)}
                >
                  <List.Item.Meta
                    title={<Text ellipsis style={{ maxWidth: 120 }}>{d.name}</Text>}
                    description={<Text type="secondary" style={{ fontSize: 11 }}>{d.fieldCount} fields</Text>}
                  />
                </List.Item>
              )}
            />
        }
      </div>

      {/* Center: canvas */}
      <div style={{ flex: 1, overflow: 'auto', background: '#e8e8e8', display: 'flex', flexDirection: 'column' }}>
        {def ? (
          <>
            {/* Toolbar */}
            <div style={{ background: '#fff', padding: '8px 16px', borderBottom: '1px solid #f0f0f0', display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              <Input
                value={def.name}
                onChange={(e) => updateDef((d) => ({ ...d, name: e.target.value }))}
                style={{ width: 220, fontWeight: 600 }}
                size="small"
              />
              <Button size="small" icon={<PlusOutlined />} onClick={addField}>Field</Button>
              <Button size="small" icon={<FontSizeOutlined />} onClick={addText}>Text</Button>
              <Button size="small" icon={<LineOutlined />} onClick={addLine}>Line</Button>
              {selected && <Button size="small" danger icon={<DeleteOutlined />} onClick={deleteSelected}>Delete</Button>}
              <div style={{ flex: 1 }} />
              {def.pageCount > 1 && (
                <Space size={2}>
                  <Button size="small" icon={<LeftOutlined />} disabled={currentPage <= 1} onClick={() => { setCurrentPage(p => p - 1); setSelected(null); }} />
                  <Text style={{ fontSize: 12 }}>Pg {currentPage}/{def.pageCount}</Text>
                  <Button size="small" icon={<RightOutlined />} disabled={currentPage >= def.pageCount} onClick={() => { setCurrentPage(p => p + 1); setSelected(null); }} />
                </Space>
              )}
              <Button size="small" icon={<EyeOutlined />} loading={previewing} onClick={handlePreview}>Preview</Button>
              <Button size="small" type="primary" icon={<SaveOutlined />} loading={saving} onClick={handleSave}>Save</Button>
            </div>
            {/* Paper */}
            <div style={{ flex: 1, overflow: 'auto', padding: 24, display: 'flex', justifyContent: 'center' }}>
              <div
                ref={canvasRef}
                style={{ position: 'relative', width: canvasW, height: canvasH, background: '#fff', boxShadow: '0 2px 8px rgba(0,0,0,0.2)', cursor: 'default', userSelect: 'none' }}
                onMouseMove={onCanvasMouseMove}
                onMouseUp={onCanvasMouseUp}
                onMouseLeave={onCanvasMouseUp}
                onClick={() => setSelected(null)}
              >
                {/* PDF page background */}
                {pageImageUrl && (
                  <img
                    src={pageImageUrl}
                    alt=""
                    draggable={false}
                    style={{ position: 'absolute', top: 0, left: 0, width: '100%', height: '100%', pointerEvents: 'none', userSelect: 'none' }}
                  />
                )}
                {/* Fields — only show elements belonging to the current page */}
                {def.fields.map((f, i) => f.page !== currentPage ? null : (
                  <div
                    key={`f-${i}`}
                    onMouseDown={(e) => onMouseDown(e, 'field', i)}
                    style={{
                      position: 'absolute', left: f.x * CANVAS_SCALE, top: f.y * CANVAS_SCALE,
                      width: f.width * CANVAS_SCALE, height: f.height * CANVAS_SCALE,
                      border: `1.5px solid ${selected?.type === 'field' && selected.index === i ? '#1677ff' : 'rgba(22,119,255,0.45)'}`,
                      background: 'rgba(22,119,255,0.05)', cursor: 'move', boxSizing: 'border-box', overflow: 'hidden',
                      outline: selected?.type === 'field' && selected.index === i ? '2px solid #1677ff' : 'none',
                    }}
                    title={f.name}
                  >
                    <span style={{ fontSize: 9, color: '#1677ff', padding: '0 2px', lineHeight: 1 }}>{f.name}</span>
                  </div>
                ))}
                {/* Static texts */}
                {def.staticTexts.map((t, i) => t.page !== currentPage ? null : (
                  <div
                    key={`t-${i}`}
                    onMouseDown={(e) => onMouseDown(e, 'text', i)}
                    style={{
                      position: 'absolute', left: t.x * CANVAS_SCALE, top: t.y * CANVAS_SCALE,
                      width: t.width * CANVAS_SCALE, height: t.height * CANVAS_SCALE,
                      cursor: 'move', boxSizing: 'border-box', overflow: 'hidden',
                      outline: selected?.type === 'text' && selected.index === i ? '2px solid #52c41a' : '1px dashed #aaa',
                      background: selected?.type === 'text' && selected.index === i ? 'rgba(82,196,26,0.08)' : 'transparent',
                      fontSize: Math.max(8, t.pointSize || 10), fontWeight: t.bold ? 700 : 400,
                      whiteSpace: 'nowrap',
                    }}
                    title={t.text}
                  >
                    <span style={{ padding: '0 2px' }}>{t.text}</span>
                  </div>
                ))}
                {/* Lines (rendered as SVG overlay) */}
                <svg style={{ position: 'absolute', top: 0, left: 0, width: '100%', height: '100%', pointerEvents: 'none' }}>
                  {def.lines.map((l, i) => l.page !== currentPage ? null : (
                    <line
                      key={`l-${i}`}
                      x1={l.x1 * CANVAS_SCALE} y1={l.y1 * CANVAS_SCALE}
                      x2={l.x2 * CANVAS_SCALE} y2={l.y2 * CANVAS_SCALE}
                      stroke={selected?.type === 'line' && selected.index === i ? '#fa8c16' : '#333'}
                      strokeWidth={Math.max(0.5, l.lineWidth)}
                    />
                  ))}
                </svg>
                {/* Clickable line hit areas */}
                {def.lines.map((l, i) => l.page !== currentPage ? null : (
                  <div
                    key={`lh-${i}`}
                    onMouseDown={(e) => onMouseDown(e, 'line', i)}
                    style={{
                      position: 'absolute',
                      left: Math.min(l.x1, l.x2) * CANVAS_SCALE - 4,
                      top: Math.min(l.y1, l.y2) * CANVAS_SCALE - 4,
                      width: (Math.abs(l.x2 - l.x1) + 8) * CANVAS_SCALE,
                      height: (Math.abs(l.y2 - l.y1) + 8) * CANVAS_SCALE,
                      cursor: 'move',
                    }}
                  />
                ))}
              </div>
            </div>
          </>
        ) : (
          <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <Space direction="vertical" align="center">
              <Text type="secondary">Select an authored form or create / import one</Text>
              <Space>
                <Button icon={<FileAddOutlined />} onClick={handleNew}>New Blank</Button>
                <Button icon={<ImportOutlined />} onClick={() => setImportOpen(true)}>Import Legacy FAP</Button>
                <Button icon={<UploadOutlined />} loading={importing} onClick={() => pdfUploadRef.current?.click()}>Upload PDF</Button>
              </Space>
            </Space>
          </div>
        )}
      </div>

      {/* Right: properties */}
      {def && (
        <div style={{ width: 260, borderLeft: '1px solid #f0f0f0', overflow: 'auto', padding: '8px 12px' }}>
          <Text strong>Properties</Text>
          <Divider style={{ margin: '8px 0' }} />
          {!selectedElement ? (
            <Form layout="vertical" size="small">
              <Title level={5} style={{ marginTop: 0 }}>Form</Title>
              <Form.Item label="Name">
                <Input value={def.name} onChange={(e) => updateDef((d) => ({ ...d, name: e.target.value }))} />
              </Form.Item>
              <Form.Item label="Description">
                <Input.TextArea rows={2} value={def.description} onChange={(e) => updateDef((d) => ({ ...d, description: e.target.value }))} />
              </Form.Item>
              <Form.Item label="Pages">
                <InputNumber min={1} max={20} value={def.pageCount} onChange={(v) => updateDef((d) => ({ ...d, pageCount: v ?? 1 }))} style={{ width: '100%' }} />
              </Form.Item>
              <Form.Item label="Width (pts)">
                <InputNumber value={def.pageWidth} onChange={(v) => updateDef((d) => ({ ...d, pageWidth: v ?? 612 }))} style={{ width: '100%' }} />
              </Form.Item>
              <Form.Item label="Height (pts)">
                <InputNumber value={def.pageHeight} onChange={(v) => updateDef((d) => ({ ...d, pageHeight: v ?? 792 }))} style={{ width: '100%' }} />
              </Form.Item>
            </Form>
          ) : selectedElement.kind === 'field' ? (
            <Form layout="vertical" size="small">
              <Title level={5} style={{ marginTop: 0 }}>Field <Tag color="blue">fillable</Tag></Title>
              <Form.Item label="Name"><Input value={selectedElement.data.name} onChange={(e) => updateField({ name: e.target.value })} /></Form.Item>
              <Form.Item label="X"><InputNumber value={Math.round(selectedElement.data.x)} onChange={(v) => updateField({ x: v ?? 0 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Y"><InputNumber value={Math.round(selectedElement.data.y)} onChange={(v) => updateField({ y: v ?? 0 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Width"><InputNumber min={1} value={Math.round(selectedElement.data.width)} onChange={(v) => updateField({ width: v ?? 50 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Height"><InputNumber min={1} value={Math.round(selectedElement.data.height)} onChange={(v) => updateField({ height: v ?? 12 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Max Length"><InputNumber min={0} value={selectedElement.data.maxLength} onChange={(v) => updateField({ maxLength: v ?? 0 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Font Size (pts)"><InputNumber min={6} max={72} value={selectedElement.data.pointSize || 10} onChange={(v) => updateField({ pointSize: v ?? 10 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Page"><InputNumber min={1} value={selectedElement.data.page} onChange={(v) => updateField({ page: v ?? 1 })} style={{ width: '100%' }} /></Form.Item>
            </Form>
          ) : selectedElement.kind === 'text' ? (
            <Form layout="vertical" size="small">
              <Title level={5} style={{ marginTop: 0 }}>Static Text</Title>
              <Form.Item label="Text"><Input value={selectedElement.data.text} onChange={(e) => updateText({ text: e.target.value })} /></Form.Item>
              <Form.Item label="X"><InputNumber value={Math.round(selectedElement.data.x)} onChange={(v) => updateText({ x: v ?? 0 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Y"><InputNumber value={Math.round(selectedElement.data.y)} onChange={(v) => updateText({ y: v ?? 0 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Width"><InputNumber min={1} value={Math.round(selectedElement.data.width)} onChange={(v) => updateText({ width: v ?? 50 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Height"><InputNumber min={1} value={Math.round(selectedElement.data.height)} onChange={(v) => updateText({ height: v ?? 12 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Font Size (pts)"><InputNumber min={6} max={72} value={selectedElement.data.pointSize || 10} onChange={(v) => updateText({ pointSize: v ?? 10 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Bold">
                <Select value={selectedElement.data.bold ? 'bold' : 'regular'} onChange={(v) => updateText({ bold: v === 'bold' })} style={{ width: '100%' }}>
                  <Select.Option value="regular">Regular</Select.Option>
                  <Select.Option value="bold">Bold</Select.Option>
                </Select>
              </Form.Item>
              <Form.Item label="Page"><InputNumber min={1} value={selectedElement.data.page} onChange={(v) => updateText({ page: v ?? 1 })} style={{ width: '100%' }} /></Form.Item>
            </Form>
          ) : (
            <Form layout="vertical" size="small">
              <Title level={5} style={{ marginTop: 0 }}>Line</Title>
              <Form.Item label="X1"><InputNumber value={Math.round(selectedElement.data.x1)} onChange={(v) => updateLine({ x1: v ?? 0 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Y1"><InputNumber value={Math.round(selectedElement.data.y1)} onChange={(v) => updateLine({ y1: v ?? 0 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="X2"><InputNumber value={Math.round(selectedElement.data.x2)} onChange={(v) => updateLine({ x2: v ?? 0 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Y2"><InputNumber value={Math.round(selectedElement.data.y2)} onChange={(v) => updateLine({ y2: v ?? 0 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Line Width"><InputNumber min={0.5} step={0.5} value={selectedElement.data.lineWidth} onChange={(v) => updateLine({ lineWidth: v ?? 1 })} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Page"><InputNumber min={1} value={selectedElement.data.page} onChange={(v) => updateLine({ page: v ?? 1 })} style={{ width: '100%' }} /></Form.Item>
            </Form>
          )}
        </div>
      )}

      {/* Import legacy modal */}
      <Modal
        title="Import Legacy FAP Form"
        open={importOpen}
        onCancel={() => setImportOpen(false)}
        onOk={handleImport}
        confirmLoading={importing}
        okText="Import"
      >
        <Form layout="vertical">
          <Form.Item label="Form Number" required>
            <Input placeholder="e.g. CA2146" value={importForm.formNumber}
              onChange={(e) => setImportForm((p) => ({ ...p, formNumber: e.target.value.toUpperCase() }))} />
          </Form.Item>
          <Form.Item label="Edition Date" required>
            <Input placeholder="e.g. 1293" value={importForm.editionDate}
              onChange={(e) => setImportForm((p) => ({ ...p, editionDate: e.target.value }))} />
          </Form.Item>
          <Form.Item label="Name (optional)">
            <Input placeholder="Defaults to form number + edition" value={importForm.name}
              onChange={(e) => setImportForm((p) => ({ ...p, name: e.target.value }))} />
          </Form.Item>
        </Form>
      </Modal>

      {/* Preview drawer */}
      <Drawer
        title={def ? `Preview: ${def.name}` : 'Preview'}
        width="60vw"
        open={showPreview}
        onClose={() => setShowPreview(false)}
        destroyOnClose={false}
      >
        {pdfUrl ? (
          <div style={{ height: 'calc(100vh - 120px)' }}>
            <Worker workerUrl="https://unpkg.com/pdfjs-dist@3.11.174/build/pdf.worker.min.js">
              <Viewer fileUrl={pdfUrl} plugins={[defaultLayoutPluginInstance]} defaultScale={SpecialZoomLevel.PageWidth} />
            </Worker>
          </div>
        ) : <Spin />}
      </Drawer>
    </div>
  );
}
