import { useState, useMemo } from 'react';
import { Table, Input, Typography, Tag, Select, Space } from 'antd';
import { useQuery } from '@tanstack/react-query';
import { fetchForms, type FormCatalogEntry, type FormClassification } from '../api/formsApi';
import { useNavigate } from 'react-router-dom';

const { Search } = Input;
const { Title } = Typography;

const CLASSIFICATION_COLORS: Record<string, string> = {
  Static:   'default',
  Variable: 'blue',
  WIP:      'green',
  Unknown:  'default',
};

const CLASSIFICATION_FILTER_OPTIONS = [
  { value: 'all',      label: 'All types' },
  { value: 'Static',   label: 'Static' },
  { value: 'Variable', label: 'Variable' },
  { value: 'WIP',      label: 'WIP' },
];

const columns = [
  {
    title: 'Form Key',
    dataIndex: 'formKey',
    key: 'formKey',
    sorter: (a: FormCatalogEntry, b: FormCatalogEntry) => a.formKey.localeCompare(b.formKey),
  },
  {
    title: 'File Name',
    dataIndex: 'fileName',
    key: 'fileName',
  },
  {
    title: 'Name / Description',
    key: 'displayName',
    render: (_: unknown, record: FormCatalogEntry) => record.displayName || record.description || '-',
  },
  {
    title: 'Type',
    dataIndex: 'classification',
    key: 'classification',
    width: 110,
    render: (val: FormClassification | undefined) =>
      val && val !== 'Unknown'
        ? <Tag color={CLASSIFICATION_COLORS[val] ?? 'default'}>{val}</Tag>
        : '-',
    sorter: (a: FormCatalogEntry, b: FormCatalogEntry) =>
      (a.classification ?? '').localeCompare(b.classification ?? ''),
  },
  {
    title: 'Section Type',
    dataIndex: 'sectionType',
    key: 'sectionType',
    render: (val: string) => val ? <Tag color="blue">{val}</Tag> : '-',
  },
  {
    title: 'Sections',
    dataIndex: 'sectionCount',
    key: 'sectionCount',
    width: 100,
    align: 'center' as const,
  },
];

export default function CatalogPage() {
  const [search, setSearch] = useState('');
  const [classFilter, setClassFilter] = useState<string>('all');
  const navigate = useNavigate();

  const { data: forms = [], isLoading } = useQuery({
    queryKey: ['forms', search],
    queryFn: () => fetchForms(search || undefined),
    staleTime: 60_000,
  });

  const filtered = useMemo(
    () => classFilter === 'all' ? forms : forms.filter(f => f.classification === classFilter),
    [forms, classFilter],
  );

  return (
    <div>
      <Title level={3}>Form Catalog</Title>
      <Space style={{ marginBottom: 24 }} wrap>
        <Search
          placeholder="Search by form number or name (e.g. CA0001, BOP, Split Bodily...)"
          allowClear
          enterButton="Search"
          size="large"
          style={{ width: 460 }}
          onSearch={setSearch}
        />
        <Select
          size="large"
          style={{ width: 140 }}
          value={classFilter}
          onChange={setClassFilter}
          options={CLASSIFICATION_FILTER_OPTIONS}
        />
      </Space>
      <Table
        dataSource={filtered}
        columns={columns}
        rowKey={(r) => `${r.formKey}-${r.fileName}`}
        loading={isLoading}
        pagination={{ pageSize: 25, showSizeChanger: true, showTotal: (t) => `${t} forms` }}
        onRow={(record) => ({
          onClick: () => {
            if (record.sourceFormNumber) {
              // Authored form: use source form number + edition date
              const edition = record.sourceEditionDate || '';
              navigate(`/convert?form=${encodeURIComponent(record.sourceFormNumber)}&edition=${encodeURIComponent(edition)}`);
            } else {
              // Legacy FAP: each row IS a specific FAP file — navigate directly by file name
              navigate(`/convert?form=${encodeURIComponent(record.fileName)}&edition=`);
            }
          },
          style: { cursor: 'pointer' },
        })}
        size="middle"
      />
    </div>
  );
}
