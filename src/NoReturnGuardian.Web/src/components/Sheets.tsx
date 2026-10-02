import { useEffect, useRef } from 'react';
import type { HostSheet, NativeAction, SnapshotView, ViewState } from '../types';
import { Button, Keys } from './Controls';

export type Sheet =
  | { kind: 'protect' }
  | { kind: 'restore'; item: SnapshotView }
  | { kind: 'undo' }
  | { kind: 'remove'; item: SnapshotView }
  | { kind: 'native'; item: SnapshotView; action: NativeAction; phase: 'confirm' | 'running' | 'failed'; text: string }
  | HostSheet;

interface SheetProps {
  sheet: Sheet;
  state: ViewState;
  onClose: () => void;
  onConfirm: (sheet: Sheet) => void;
  onNative: (item: SnapshotView, action: NativeAction) => void;
  onLaunch: () => void;
}

// 居中的玻璃面板。危险动作的焦点默认落在“取消”上；Esc 关闭（恢复进行中除外）。
export function SheetLayer({ sheet, state, onClose, onConfirm, onNative, onLaunch }: SheetProps) {
  const panel = useRef<HTMLDivElement>(null);
  const locked = sheet.kind === 'native' && sheet.phase === 'running';

  useEffect(() => {
    const target = panel.current?.querySelector<HTMLElement>('[data-autofocus]') ?? panel.current;
    target?.focus();
    // 原生面板从确认走到进度、再到失败时，焦点跟到新出现的按钮上。
  }, [sheet.kind, sheet.kind === 'native' ? sheet.phase : null]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !locked) onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [locked, onClose]);

  return (
    <div className="sheet-layer" onPointerDown={(event) => event.target === event.currentTarget && !locked && onClose()}>
      <div className={`sheet sheet-${sheet.kind}${locked ? ' working' : ''}`} ref={panel} role="dialog" aria-modal tabIndex={-1}>
        <SheetBody sheet={sheet} state={state} onClose={onClose} onConfirm={onConfirm} onNative={onNative} onLaunch={onLaunch} />
      </div>
    </div>
  );
}

function Actions({ cancel = '取消', confirm, danger, onCancel, onConfirm }: {
  cancel?: string;
  confirm: string;
  danger?: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <div className="sheet-actions">
      <Button variant="ghost" data-autofocus onClick={onCancel}>{cancel}</Button>
      <Button variant={danger ? 'danger-solid' : 'primary'} onClick={onConfirm}>{confirm}</Button>
    </div>
  );
}

function SheetBody({ sheet, state, onClose, onConfirm, onNative, onLaunch }: SheetProps) {
  switch (sheet.kind) {
    case 'protect':
      return (
        <>
          <h2>保护当前战备</h2>
          <p>请确认你正在兵营里。</p>
          <Actions confirm="在兵营，保护" onCancel={onClose} onConfirm={() => onConfirm(sheet)} />
        </>
      );
    case 'restore':
      return (
        <>
          <h2>恢复 {sheet.item.time} 的战备</h2>
          <p>{sheet.item.stamp}。当前存档会先自动留档，可以撤销。</p>
          <Actions confirm="恢复" danger onCancel={onClose} onConfirm={() => onConfirm(sheet)} />
        </>
      );
    case 'undo':
      return (
        <>
          <h2>撤销最近一次恢复</h2>
          <p>存档回到恢复之前的样子。</p>
          <Actions confirm="撤销" danger onCancel={onClose} onConfirm={() => onConfirm(sheet)} />
        </>
      );
    case 'remove':
      return (
        <>
          <h2>删除 {sheet.item.time} 的{sheet.item.kind === '恢复前' ? '现场' : '战备'}</h2>
          <p>只删除守护器里的这一份，游戏存档不受影响。</p>
          <Actions confirm="删除" danger onCancel={onClose} onConfirm={() => onConfirm(sheet)} />
        </>
      );
    case 'native':
      return <NativeBody sheet={sheet} state={state} onClose={onClose} onNative={onNative} />;
    case 'interrupted':
      return (
        <>
          <h2>上次恢复没有完成</h2>
          <p>把存档回滚到恢复之前。完成前不要启动游戏。</p>
          <Actions cancel="稍后" confirm="回滚" danger onCancel={onClose} onConfirm={() => onConfirm(sheet)} />
        </>
      );
    case 'cleanup':
      return (
        <>
          <h2>开启自动清理</h2>
          <p>现在会删除 {sheet.removals} 份旧快照，释放约 {sheet.size}。之后每类只留最近 3 份，正在用的不删。删除后无法找回。</p>
          <Actions confirm="开启并删除" danger onCancel={onClose} onConfirm={() => onConfirm(sheet)} />
        </>
      );
    case 'written':
      return (
        <>
          <h2>战备已写回</h2>
          <p>启动游戏，看看是否回到了 {sheet.time} 的兵营。</p>
          <div className="sheet-actions">
            <Button variant="ghost" onClick={onClose}>好</Button>
            <Button variant="primary" data-autofocus onClick={() => { onLaunch(); onClose(); }}>启动游戏</Button>
          </div>
        </>
      );
    case 'failed':
      return (
        <>
          <h2>{sheet.title}</h2>
          <p>{sheet.text}</p>
          <div className="sheet-actions">
            <Button variant="primary" data-autofocus onClick={onClose}>好</Button>
          </div>
        </>
      );
  }
}

// 恢复兵营、重开战斗：从哪里开始由宿主按游戏当时的样子决定，这里只确认、只报进度。
function NativeBody({ sheet, state, onClose, onNative }: {
  sheet: Extract<Sheet, { kind: 'native' }>;
  state: ViewState;
  onClose: () => void;
  onNative: (item: SnapshotView, action: NativeAction) => void;
}) {
  const restart = sheet.action === 'restart';
  const title = restart ? '重开战斗' : '恢复兵营';
  const keys = restart ? state.hotkeys.restart : state.hotkeys.recover;
  if (sheet.phase === 'confirm') {
    return (
      <>
        <h2>{title}</h2>
        <p className="sheet-sub">{sheet.item.time} 的{sheet.item.kind}战备 · {sheet.item.stamp}</p>
        <p>
          {restart
            ? '回到这份战备，按原路线自动出发。停在结算页、赴死之旅菜单、战斗中，或刚回到它的兵营里，都可以重开；战斗中会先放弃本场。'
            : '回到这份战备的兵营。停在结算页、赴死之旅菜单或战斗中都可以；战斗中会先放弃本场。当前进度会先自动留档。'}
        </p>
        {sheet.item.state === 'restart' && <p>这份是出发之后才存下的旧版自动保存，回不了兵营，只能重开它的战斗。</p>}
        {keys && (
          <p className="sheet-keys">
            <span>游戏里可以直接按</span>
            <Keys combo={keys} />
          </p>
        )}
        <div className="sheet-actions">
          <Button variant="ghost" data-autofocus onClick={onClose}>取消</Button>
          <Button variant="danger-solid" onClick={() => onNative(sheet.item, sheet.action)}>{title}</Button>
        </div>
      </>
    );
  }

  const running = sheet.phase === 'running';
  return (
    <>
      <h2>{title}</h2>
      <p className="sheet-sub">{sheet.item.time} 的战备 · {sheet.item.stamp}</p>
      {running && <div className="sheet-track" aria-hidden />}
      <p className={`sheet-progress${sheet.phase === 'failed' ? ' failed' : ''}`} aria-live="polite">
        {sheet.text}
      </p>
      {running && <p className="sheet-note">请保持游戏在前台</p>}
      {!running && (
        <div className="sheet-actions">
          <Button variant="ghost" data-autofocus onClick={onClose}>关闭</Button>
        </div>
      )}
    </>
  );
}
