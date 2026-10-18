/**
 * "More diagram types" (QA P5): ER, UML class, BPMN, swimlane and org-chart diagrams are not document types of their own
 * (the model has flowchart, architecture, freeform and sequence), they are shapes the shape panel offers inside one of
 * those. Buried in palette groups they were invisible to anyone who did not already know, so each is a starting point in
 * the New diagram menu: an ordinary flowchart or architecture document that opens with the shapes of that kind already on
 * the canvas, ready to rename and connect, and whose palette group is the one that adds more. No new document type, no
 * model change: a template is just the nodes and edges a blank document of its type begins with.
 */

import { poolWithLanes } from '../model/swimlanes.ts';
import type { Document, DocumentType, Edge, Node } from '../model/types.ts';

export interface DiagramTemplate {
  id: string;
  /** The menu entry. */
  label: string;
  /** One line under it in the empty state. */
  hint: string;
  /** The document type the diagram is made as. */
  type: DocumentType;
  /** The title a new one is given, which the editor opens ready to rename. */
  title: string;
  nodes: () => Node[];
  edges: () => Edge[];
}

const meta = { createdAt: '1970-01-01T00:00:00.000Z', updatedAt: '1970-01-01T00:00:00.000Z', createdBy: 'template' };

function node(id: string, kind: string, label: string, shape: string, x: number, y: number, w: number, h: number, extra: Partial<Node> = {}): Node {
  return { id, kind, label, shape, geometry: { x, y, w, h }, ...extra };
}

function edge(id: string, kind: string, from: string, to: string, label?: string): Edge {
  return { id, kind, ...(label === undefined ? {} : { label }), from: { node: from }, to: { node: to }, routing: { mode: 'orthogonal' } };
}

const ER_SHAPE = 'custom:er/entity';
const CLASS_SHAPE = 'custom:uml/class';
const ORG_BOX = (name: string): string => `custom:org/${name}`;
const BPMN = (name: string): string => `custom:bpmn/${name}`;

export const DIAGRAM_TEMPLATES: readonly DiagramTemplate[] = [
  {
    id: 'er',
    label: 'ER diagram',
    hint: 'Tables, their columns and the relationships between them',
    type: 'architecture',
    title: 'Untitled ER diagram',
    nodes: () => [
      node('t_customers', 'entity', 'Customers', ER_SHAPE, 40, 40, 264, 110, { props: { columns: ['PK Id int not null', 'Name nvarchar(100) not null', 'Email nvarchar(200) null'] } }),
      node('t_orders', 'entity', 'Orders', ER_SHAPE, 440, 40, 264, 110, { props: { columns: ['PK Id int not null', 'FK CustomerId int not null', 'PlacedAt datetime not null'] } }),
    ],
    edges: () => [edge('e_1', 'association', 't_orders', 't_customers', 'CustomerId')],
  },
  {
    id: 'uml-class',
    label: 'UML class diagram',
    hint: 'Classes with attributes and operations, and how they relate',
    type: 'architecture',
    title: 'Untitled class diagram',
    nodes: () => [
      node('n_order', 'class', 'Order', CLASS_SHAPE, 40, 40, 176, 112, { props: { attributes: ['- id: int', '- total: decimal'], operations: ['+ pay(): void'] } }),
      node('n_line', 'class', 'OrderLine', CLASS_SHAPE, 360, 40, 176, 112, { props: { attributes: ['- sku: string', '- quantity: int'], operations: [] } }),
    ],
    edges: () => [edge('e_1', 'association', 'n_order', 'n_line')],
  },
  {
    id: 'bpmn',
    label: 'BPMN process',
    hint: 'Events, tasks and gateways, with the BPMN shapes in the panel',
    type: 'flowchart',
    title: 'Untitled BPMN process',
    nodes: () => [
      node('n_start', 'bpmn-start', 'Start', BPMN('start'), 40, 100, 40, 40),
      node('n_task', 'bpmn-task', 'Do the work', BPMN('task'), 140, 85, 100, 70),
      node('n_gate', 'bpmn-exclusive-gateway', 'Done?', BPMN('exclusive-gateway'), 300, 95, 50, 50),
      node('n_end', 'bpmn-end', 'End', BPMN('end'), 410, 100, 40, 40),
    ],
    edges: () => [edge('e_1', 'flow', 'n_start', 'n_task'), edge('e_2', 'flow', 'n_task', 'n_gate'), edge('e_3', 'flow', 'n_gate', 'n_end', 'yes')],
  },
  {
    id: 'swimlane',
    label: 'Swimlane diagram',
    hint: 'A pool with lanes, one per team or role',
    type: 'flowchart',
    title: 'Untitled swimlane diagram',
    nodes: () => {
      const blank: Document = { id: 'doc_template', schemaVersion: 1, title: '', type: 'flowchart', nodes: [], edges: [], meta };
      return poolWithLanes(blank, node('n_pool', 'pool', 'Process', 'custom:swimlane/pool', 40, 40, 640, 320));
    },
    edges: () => [],
  },
  {
    id: 'org-chart',
    label: 'Org chart',
    hint: 'People and roles, with who reports to whom',
    type: 'architecture',
    title: 'Untitled org chart',
    nodes: () => [
      node('n_ceo', 'executive', 'Alex Morgan\nChief Executive', ORG_BOX('executive'), 280, 40, 180, 64),
      node('n_eng', 'role', 'Sam Lee\nHead of Engineering', ORG_BOX('role'), 100, 180, 180, 64),
      node('n_ops', 'role', 'Jo Patel\nHead of Operations', ORG_BOX('role'), 460, 180, 180, 64),
    ],
    edges: () => [edge('e_1', 'flow', 'n_ceo', 'n_eng'), edge('e_2', 'flow', 'n_ceo', 'n_ops')],
  },
];
