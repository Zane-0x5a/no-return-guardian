import { useCallback, useEffect, useRef, useState } from 'react';
import { host } from './bridge';
import { Button } from './components/Controls';
import { Icon } from './components/Icons';
import { Ledger } from './components/Ledger';
import { Field } from './components/Field';
import { Settings } from './components/Settings';
import { type Sheet, SheetLayer } from './components/Sheets';
import { Stage } from './components/Stage';
import type { Command, HostMessage, NativeAction, SnapshotView, Tone, ViewState } from './types';

// 恢复前快照平时用不到，默认不进清单；“撤销最近恢复”照常可用。
function listed(state: ViewState): SnapshotView[] {
  return state.settings.showUndo ? state.snapshots : state.snapshots.filter((item) => !item.preRestore);
}

interface Toast {
  id: number;
  text: string;
  tone: Tone;
}

export function App() {
  const [state, setState] = useState<ViewState | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [fresh, setFresh] = useState<string | null>(null);
  const [sheet, setSheet] = useState<Sheet | null>(null);
  const [view, setView] = useState<'ledger' | 'settings'>('ledger');
  const [toasts, setToasts] = useState<Toast[]>([]);
  const focusNonce = useRef(0);
  const sheetRef = useRef<Sheet | null>(null);
  sheetRef.current = sheet;

  const send = useCallback((command: Command) => host.send(command), []);

  const toast = useCallback((text: string, tone: Tone) => {
    const id = Date.now() + Math.random();
    setToasts((items) => [...items.slice(-2), { id, text, tone }]);
    window.setTimeout(() => setToasts((items) => items.filter((item) => item.id !== id)), tone === 'danger' ? 8000 : 4200);
  }, []);

  useEffect(() => {
    const off = host.subscribe((message: HostMessage) => {
      switch (message.type) {
        case 'state': {
          const next = message.state;
          const visible = listed(next);
          setState(next);
          if (next.focus.nonce !== focusNonce.current) {
            const isFirst = focusNonce.current === 0;
            focusNonce.current = next.focus.nonce;
            if (next.focus.id && visible.some((item) => item.id === next.focus.id)) {
              setSelected(next.focus.id);
              if (!isFirst) {
                setFresh(next.focus.id);
                window.setTimeout(() => setFresh(null), 2400);
              }
            }
          }
          setSelected((current) =>
            current && visible.some((item) => item.id === current)
              ? current
              : visible.find((item) => item.state === 'ready')?.id ?? visible[0]?.id ?? null,
          );
          break;
        }
        case 'toast':
          toast(message.text, message.tone);
          break;
        case 'sheet':
          setSheet(message.sheet);
          break;
        case 'recovery': {
          const current = sheetRef.current;
          if (current?.kind !== 'native') {
            if (message.phase === 'done') toast(message.text, 'signal');
            break;
          }
          if (message.phase === 'done') {
            setSheet(null);
            send({ name: 'native-close' });
            toast(message.text, 'signal');
          } else {
            setSheet({ ...current, phase: message.phase, text: message.text });
          }
          break;
        }
      }
    });
    send({ name: 'ready' });
    return () => {
      off();
    };
  }, [send, toast]);

  const closeSheet = useCallback(() => {
    if (sheetRef.current?.kind === 'native') send({ name: 'native-close' });
    setSheet(null);
  }, [send]);

  const confirm = useCallback(
    (current: Sheet) => {
      setSheet(null);
      switch (current.kind) {
        case 'protect': send({ name: 'protect' }); break;
        case 'restore': send({ name: 'restore', id: current.item.id }); break;
        case 'undo': send({ name: 'undo' }); break;
        case 'remove': send({ name: 'remove', id: current.item.id }); break;
        case 'interrupted': send({ name: 'interrupted-confirm' }); break;
        case 'cleanup': send({ name: 'cleanup-confirm', token: current.token }); break;
        default: break;
      }
    },
    [send],
  );

  const restore = useCallback(
    (item: SnapshotView) => {
      if (item.restore === 'native') {
        send({ name: 'native-open', id: item.id, action: 'hideout' });
        setSheet({ kind: 'native', item, action: 'hideout', phase: 'confirm', text: '' });
      } else if (item.restore === 'file') {
        setSheet({ kind: 'restore', item });
      }
    },
    [send],
  );

  const restart = useCallback(
    (item: SnapshotView) => {
      if (!item.restart) return;
      send({ name: 'native-open', id: item.id, action: 'restart' });
      setSheet({ kind: 'native', item, action: 'restart', phase: 'confirm', text: '' });
    },
    [send],
  );

  const native = useCallback(
    (item: SnapshotView, action: NativeAction) => {
      setSheet({ kind: 'native', item, action, phase: 'running', text: '正在核对游戏状态' });
      send({ name: 'native-start', id: item.id, action });
    },
    [send],
  );

  // 拖动滑块时先在本地预览，停手后再告诉宿主保存。
  const [transparency, setTransparency] = useState<number | null>(null);
  const saveTransparency = useRef<number | undefined>(undefined);
  const changeTransparency = useCallback(
    (value: number) => {
      setTransparency(value);
      window.clearTimeout(saveTransparency.current);
      saveTransparency.current = window.setTimeout(() => send({ name: 'panel-transparency', value }), 300);
    },
    [send],
  );

  if (!state) return <div className="boot" />;
  const working = state.working || (sheet?.kind === 'native' && sheet.phase === 'running');
  const panelTransparency = transparency ?? state.settings.panelTransparency;

  return (
    <div
      className={`app mode-${state.mode}${sheet ? ' has-sheet' : ''}`}
      style={{ '--panel-alpha': (100 - panelTransparency) / 100 } as React.CSSProperties}
    >
      <Field mode={state.mode} working={working} />

      <Stage
        state={state}
        onProtect={() => setSheet({ kind: 'protect' })}
        onUndo={() => setSheet({ kind: 'undo' })}
        onLaunch={() => send({ name: 'launch' })}
        onChooseProfile={() => send({ name: 'choose-profile' })}
        onWarm={() => send({ name: 'warm' })}
        onOpenRelease={() => send({ name: 'open-release' })}
      />

      <section className="panel" aria-label={view === 'ledger' ? '战备' : '设置'}>
        <header className="panel-head">
          {view === 'settings' ? (
            <button type="button" className="panel-back" onClick={() => setView('ledger')}>
              <Icon.back />
              <span>设置</span>
            </button>
          ) : (
            <h2 className="panel-title">
              战备
              {state.snapshots.length > 0 && <span className="panel-count">{state.library.preparations}</span>}
            </h2>
          )}
          <div className="window-controls">
            {view === 'ledger' && (
              <button type="button" className="chrome" aria-label="设置" title="设置" onClick={() => setView('settings')}>
                <Icon.settings />
              </button>
            )}
            <button type="button" className="chrome" aria-label="最小化" title="最小化" onClick={() => send({ name: 'window', action: 'minimize' })}>
              <Icon.minus />
            </button>
            <button type="button" className="chrome" aria-label="关闭到托盘" title="关闭到托盘" onClick={() => send({ name: 'window', action: 'close' })}>
              <Icon.close />
            </button>
          </div>
        </header>

        <div className={`panel-body view-${view}`}>
          {view === 'ledger' ? (
            <Ledger
              snapshots={listed(state)}
              selected={selected}
              fresh={fresh}
              canRemove={state.actions.remove}
              onSelect={setSelected}
              onRestore={restore}
              onRestart={restart}
              onRemove={(item) => setSheet({ kind: 'remove', item })}
            />
          ) : (
            <Settings
              state={state}
              onAutoProtect={(value) => send({ name: 'auto-protect', value })}
              onStartup={(value) => send({ name: 'startup', value })}
              onChooseProfile={() => send({ name: 'choose-profile' })}
              onOpenSaves={() => send({ name: 'open-saves' })}
              onOpenBackups={() => send({ name: 'open-backups' })}
              onAutoCleanup={(value) => send({ name: 'auto-cleanup', value })}
              onShowUndo={(value) => send({ name: 'show-undo', value })}
              panelTransparency={panelTransparency}
              onPanelTransparency={changeTransparency}
              onCheckUpdates={(value) => send({ name: 'check-updates', value })}
              onOpenRelease={() => send({ name: 'open-release' })}
            />
          )}
        </div>
      </section>

      <div className="toasts" aria-live="polite">
        {toasts.map((item) => (
          <div key={item.id} className={`toast toast-${item.tone}`}>
            {item.text}
          </div>
        ))}
      </div>

      {sheet && (
        <SheetLayer
          sheet={sheet}
          state={state}
          onClose={closeSheet}
          onConfirm={confirm}
          onNative={native}
          onLaunch={() => send({ name: 'launch' })}
        />
      )}
      {!host.embedded && <PreviewBadge />}
    </div>
  );
}

function PreviewBadge() {
  const scenes = ['guarding', 'observing', 'idle', 'alert', 'empty', 'noprofile', 'blocked'];
  const current = new URLSearchParams(location.search).get('scene') ?? 'guarding';
  return (
    <nav className="preview-badge">
      {scenes.map((scene) => (
        <Button key={scene} variant={scene === current ? 'line' : 'ghost'} onClick={() => (location.search = `?scene=${scene}`)}>
          {scene}
        </Button>
      ))}
    </nav>
  );
}
