import type { Command, HostMessage, Mode, SnapshotView, ViewState } from './types';

// 浏览器预览用的模拟宿主：?scene=guarding|observing|idle|alert|empty|noprofile|blocked
// 只模拟界面会看到的结果，不代表真实门控。

type Listener = (message: HostMessage) => void;

const base: SnapshotView[] = [
  { id: 's9', time: '21:16', stamp: '2026年10月1日 21:16:42', day: '今天', kind: '恢复前', state: 'scene', restore: null, restart: false, departure: false, preRestore: true },
  { id: 's8', time: '21:00', stamp: '2026年10月1日 21:00:26', day: '今天', kind: '出发前', state: 'ready', restore: 'native', restart: true, departure: true, preRestore: false },
  { id: 's7', time: '20:47', stamp: '2026年10月1日 20:47:54', day: '今天', kind: '出发后', state: 'restart', restore: null, restart: true, departure: true, preRestore: false },
  { id: 's6', time: '20:13', stamp: '2026年10月1日 20:13:39', day: '今天', kind: '手动', state: 'ready', restore: 'native', restart: false, departure: false, preRestore: false },
  { id: 's5', time: '15:31', stamp: '2026年10月1日 15:31:49', day: '今天', kind: '恢复前', state: 'scene', restore: null, restart: false, departure: false, preRestore: true },
  { id: 's3', time: '15:16', stamp: '2026年10月1日 15:16:08', day: '今天', kind: '手动', state: 'ready', restore: 'native', restart: true, departure: true, preRestore: false },
  { id: 's2', time: '22:05', stamp: '2026年9月30日 22:05:37', day: '9月30日', kind: '手动', state: 'ready', restore: 'native', restart: false, departure: false, preRestore: false },
  { id: 's1', time: '21:12', stamp: '2026年9月28日 21:12:03', day: '9月28日', kind: '旧版', state: 'locked', restore: null, restart: false, departure: false, preRestore: false },
];

const scenes: Record<string, Partial<ViewState> & { mode: Mode }> = {
  guarding: { mode: 'guarding', headline: '守护中', detail: '游戏运行中 · 4 份可恢复', gameRunning: true },
  observing: { mode: 'observing', headline: '观察中', detail: '游戏运行中 · 进入兵营后可保护', gameRunning: true },
  idle: { mode: 'idle', headline: '待机', detail: '游戏未运行 · 4 份可恢复', gameRunning: false },
  alert: { mode: 'alert', headline: '征途疑似结束', detail: '可以回兵营或重开这场战斗', gameRunning: true },
  empty: { mode: 'observing', headline: '观察中', detail: '游戏运行中 · 进入兵营后可保护', gameRunning: true },
  noprofile: { mode: 'idle', headline: '待机', detail: '没有找到游戏存档', gameRunning: false },
  blocked: { mode: 'alert', headline: '恢复未完成', detail: '退出游戏后重新打开守护器', gameRunning: true },
};

export function createMockHost() {
  const listeners = new Set<Listener>();
  const params = new URLSearchParams(location.search);
  const sceneName = params.get('scene') ?? 'guarding';
  const scene = scenes[sceneName] ?? scenes.guarding;
  let nonce = 1;
  let snapshots = sceneName === 'empty' || sceneName === 'noprofile' ? [] : base.map((item) => ({ ...item }));
  const running = scene.gameRunning ?? true;
  if (!running) {
    snapshots = snapshots.map((item) => ({ ...item, restore: item.state === 'ready' ? 'file' : null, restart: false }));
  }

  let state: ViewState = {
    headline: scene.headline ?? '守护中',
    detail: scene.detail ?? '',
    mode: scene.mode,
    gameRunning: running,
    lastProtected: snapshots.length ? { time: '21:00', day: '今天' } : null,
    hotkeys: running ? { recover: 'Ctrl+Alt+F9', restart: 'Ctrl+Alt+F10' } : { recover: null, restart: null },
    profile: { found: sceneName !== 'noprofile', name: '76561198000000000' },
    snapshots,
    focus: { id: snapshots.find((item) => item.state === 'ready')?.id ?? null, nonce },
    actions: {
      protect: sceneName !== 'noprofile' && sceneName !== 'blocked',
      undo: !running && sceneName !== 'blocked',
      launch: !running,
      cleanup: snapshots.length > 0,
      remove: sceneName !== 'blocked',
      warm: false,
    },
    blocked: sceneName === 'blocked' ? '上次恢复没有完成，请先退出游戏' : null,
    settings: {
      autoProtect: true,
      autoProtectAvailable: true,
      startup: false,
      autoCleanup: false,
      showUndo: params.get('undo') === '1',
      panelTransparency: Number(params.get('glass') ?? 60),
      profilePath: 'C:\\Users\\Player\\Documents\\The Last of Us Part II\\76561198000000000',
    },
    library: { count: snapshots.length, size: '70.9 MB', preparations: 4, scenes: 3 },
    busy: false,
    working: false,
  };

  const emit = (message: HostMessage) => {
    // 模拟跨进程消息的异步投递。
    setTimeout(() => listeners.forEach((listener) => listener(message)), 30);
  };
  const push = (patch: Partial<ViewState>) => {
    state = { ...state, ...patch };
    emit({ type: 'state', state });
  };
  const toast = (text: string, tone: 'neutral' | 'signal' | 'danger' = 'signal') => emit({ type: 'toast', text, tone });

  const handle = (command: Command) => {
    switch (command.name) {
      case 'ready':
        push({});
        if (params.get('sheet') === 'interrupted') emit({ type: 'sheet', sheet: { kind: 'interrupted' } });
        break;
      case 'protect': {
        const now = new Date();
        const time = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
        const item: SnapshotView = {
          id: 'n' + Date.now(), time, stamp: `2026年10月1日 ${time}:00`, day: '今天', kind: '手动', state: 'ready',
          restore: state.gameRunning ? 'native' : 'file', restart: false, departure: false, preRestore: false,
        };
        nonce += 1;
        push({ snapshots: [item, ...state.snapshots], focus: { id: item.id, nonce }, lastProtected: { time, day: '今天' }, mode: 'guarding', headline: '守护中' });
        toast('战备已保护');
        break;
      }
      case 'restore':
        setTimeout(() => emit({ type: 'sheet', sheet: { kind: 'written', time: state.snapshots.find((item) => item.id === command.id)?.time ?? '' } }), 600);
        break;
      case 'native-open':
        // ?refuse=1：宿主在打开面板时就拒绝（例如游戏刚退出）。
        if (params.get('refuse') === '1') {
          emit({ type: 'recovery', phase: 'failed', text: '需要游戏正在运行，而且只开着一个游戏。' });
        }
        break;
      case 'native-start': {
        const steps = command.action === 'hideout'
          ? ['正在走完结算', '结算结束，正在保存现场', '游戏正在载入战备']
          : ['正在走完结算', '游戏正在载入战备', '已回到战备，正在出发'];
        steps.forEach((text, index) => setTimeout(() => emit({ type: 'recovery', phase: 'running', text }), 900 * index + 200));
        setTimeout(() => {
          if (params.get('fail') === '1') {
            emit({ type: 'recovery', phase: 'failed', text: '现在的画面不能开始恢复：需要停在死亡结算页、赴死之旅菜单，或者战斗中。游戏没有被改动。' });
          } else {
            emit({ type: 'recovery', phase: 'done', text: command.action === 'restart' ? '已重开 21:00 战备的同一遭遇' : '已回到 21:00 的战备' });
          }
        }, 900 * steps.length + 600);
        break;
      }
      case 'undo':
        toast('已撤销最近一次恢复');
        break;
      case 'remove':
        push({ snapshots: state.snapshots.filter((item) => item.id !== command.id) });
        toast('已删除', 'neutral');
        break;
      case 'launch':
        toast('正在启动游戏', 'neutral');
        break;
      case 'auto-cleanup':
        if (command.value) emit({ type: 'sheet', sheet: { kind: 'cleanup', token: 't1', removals: 3, size: '26.4 MB' } });
        else push({ settings: { ...state.settings, autoCleanup: false } });
        break;
      case 'cleanup-confirm':
        push({ snapshots: state.snapshots.slice(0, 5), settings: { ...state.settings, autoCleanup: true } });
        toast('已开启自动清理，删除了 3 份旧快照', 'neutral');
        break;
      case 'show-undo':
        push({ settings: { ...state.settings, showUndo: command.value } });
        break;
      case 'panel-transparency':
        push({ settings: { ...state.settings, panelTransparency: command.value } });
        break;
      case 'interrupted-confirm':
        toast('已回滚到恢复前的现场');
        break;
      case 'auto-protect':
        push({ settings: { ...state.settings, autoProtect: command.value } });
        break;
      case 'startup':
        push({ settings: { ...state.settings, startup: command.value } });
        break;
      default:
        break;
    }
  };

  return {
    embedded: false,
    send: handle,
    subscribe(listener: Listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}
