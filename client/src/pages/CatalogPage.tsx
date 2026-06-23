import { useState } from 'react';
import { Table, Input, Typography, Tag } from 'antd';
import { useQuery } from '@tanstack/react-query';
import { fetchForms, type FormCatalogEntry } from '../api/formsApi';
import { useNavigate } from 'react-router-dom';

const { Search } = Input;
const { Title } = Typography;

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
  const navigate = useNavigate();

  const { data: forms = [], isLoading } = useQuery({
    queryKey: ['forms', search],
    queryFn: () => fetchForms(search || undefined),
    staleTime: 60_000,
  });

  return (
    <div>
      <Title level={3}>Form Catalog</Title>
      <Search
        placeholder="Search by form number or name (e.g. CA0001, BOP, Split Bodily...)"
        allowClear
        enterButton="Search"
        size="large"
        style={{ maxWidth: 500, marginBottom: 24 }}
        onSearch={setSearch}
      />
      <Table
        dataSource={forms}
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
