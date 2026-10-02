import { useEffect, useMemo, useRef } from 'react';
import type { SnapshotView } from '../types';
import { Button, trackPointer } from './Controls';
import { Icon } from './Icons';

interface LedgerProps {
  snapshots: SnapshotView[];
  selected: string | null;
  fresh: string | null;
  canRemove: boolean;
  onSelect: (id: string) => void;
  onRestore: (item: SnapshotView) => void;
  onRestart: (item: SnapshotView) => void;
  onRemove: (item: SnapshotView) => void;
}

// 可恢复是常态，不标；只标出例外。
const stateLabel: Record<Exclude<SnapshotView['state'], 'ready'>, string> = {
  restart: '只能重开',
  scene: '现场',
  locked: '不可恢复',
  corrupt: '已损坏',
};

// 战备清单：按天分组、细线分隔的列表。选中的一行展开，露出它自己的动作。
export function Ledger({ snapshots, selected, fresh, canRemove, onSelect, onRestore, onRestart, onRemove }: LedgerProps) {
  const groups = useMemo(() => {
    const result: { day: string; items: SnapshotView[] }[] = [];
    snapshots.forEach((item) => {
      const last = result[result.length - 1];
      if (last && last.day === item.day) last.items.push(item);
      else result.push({ day: item.day, items: [item] });
    });
    return result;
  }, [snapshots]);
  const list = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!selected || !list.current) return;
    const row = list.current.querySelector<HTMLElement>(`[data-id="${CSS.escape(selected)}"]`);
    row?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    // 用键盘移动选择时，焦点跟着新的一行走。
    if (list.current.contains(document.activeElement)) row?.querySelector<HTMLElement>('.row-head')?.focus({ preventScroll: true });
  }, [selected]);

  const move = (offset: number) => {
    if (!snapshots.length) return;
    const index = snapshots.findIndex((item) => item.id === selected);
    const next = snapshots[Math.min(snapshots.length - 1, Math.max(0, (index < 0 ? 0 : index) + offset))];
    onSelect(next.id);
  };

  // 只在清单本身或行头上响应；行里的按钮自己处理按键。
  const onKey = (event: React.KeyboardEvent) => {
    if (event.target !== event.currentTarget && !(event.target as HTMLElement).classList.contains('row-head')) return;
    const current = snapshots.find((item) => item.id === selected);
    switch (event.key) {
      case 'ArrowDown': move(1); break;
      case 'ArrowUp': move(-1); break;
      case 'PageDown': move(6); break;
      case 'PageUp': move(-6); break;
      case 'Home': move(-snapshots.length); break;
      case 'End': move(snapshots.length); break;
      case 'Enter':
        if (current?.restore) onRestore(current);
        else if (current?.restart) onRestart(current);
        break;
      case 'Delete': if (current && canRemove) onRemove(current); break;
      default: return;
    }
    event.preventDefault();
  };

  if (!snapshots.length) {
    return (
      <div className="ledger-empty">
        <p className="ledger-empty-title">还没有战备</p>
        <p className="ledger-empty-hint">进入兵营后，点“保护当前战备”</p>
      </div>
    );
  }

  return (
    <div className="ledger" ref={list} role="listbox" aria-label="战备" tabIndex={0} onKeyDown={onKey}>
      {groups.map((group) => (
        <section className="ledger-group" key={group.day}>
          <h3 className="ledger-day">{group.day}</h3>
          {group.items.map((item) => {
            const active = item.id === selected;
            return (
              <div
                key={item.id}
                data-id={item.id}
                className={`row row-${item.state}${active ? ' active' : ''}${item.id === fresh ? ' fresh' : ''}`}
                role="option"
                aria-selected={active}
                onPointerMove={trackPointer}
              >
                <button
                  type="button"
                  className="row-head"
                  tabIndex={active ? 0 : -1}
                  onClick={() => onSelect(item.id)}
                  onDoubleClick={() => (item.restore ? onRestore(item) : item.restart && onRestart(item))}
                >
                  <span className="row-time">{item.time}</span>
                  <span className="row-kind">
                    {item.kind}
                    {item.departure && <span className="row-note">可重开</span>}
                  </span>
                  <span className="row-state">{item.state !== 'ready' && stateLabel[item.state]}</span>
                </button>
                <div className="row-body" aria-hidden={!active}>
                  <div className="row-body-inner">
                    <span className="row-stamp">
                      {item.stamp}
                      {item.state === 'restart' && !item.restart && <span className="row-hint">游戏运行时可以重开它的战斗</span>}
                    </span>
                    <div className="row-actions">
                      {item.restore && (
                        <Button variant="danger" icon={<Icon.restore />} tabIndex={active ? 0 : -1} onClick={() => onRestore(item)}>
                          恢复兵营
                        </Button>
                      )}
                      {item.restart && (
                        <Button variant="danger" icon={<Icon.mark />} tabIndex={active ? 0 : -1} onClick={() => onRestart(item)}>
                          重开战斗
                        </Button>
                      )}
                      <Button
                        variant="icon"
                        icon={<Icon.trash />}
                        aria-label="删除这份快照"
                        title="删除"
                        disabled={!canRemove}
                        tabIndex={active ? 0 : -1}
                        onClick={() => onRemove(item)}
                      />
                    </div>
                  </div>
                </div>
              </div>
            );
          })}
        </section>
      ))}
    </div>
  );
}
