// 宿主（NoReturnGuardian.exe）与界面之间的数据契约。宿主每次状态变化推送完整的 ViewState；
// 界面只发命令，所有门控在宿主侧重新核对。

export type Mode = 'idle' | 'observing' | 'guarding' | 'alert';

/** restart：出发后才存下的旧版自动保存，回不了兵营，只能重开它的战斗 */
export type SnapshotState = 'ready' | 'restart' | 'scene' | 'locked' | 'corrupt';

export interface SnapshotView {
  id: string;
  /** 本地时间 HH:mm */
  time: string;
  /** 本地完整时间 yyyy年M月d日 HH:mm:ss */
  stamp: string;
  /** 分组：今天 / 昨天 / 9月30日 */
  day: string;
  /** 手动 / 出发前 / 出发后 / 恢复前 / 旧版 / 损坏 */
  kind: string;
  state: SnapshotState;
  /** 现在能否恢复兵营，以及走哪条路 */
  restore: 'native' | 'file' | null;
  /** 现在能否重开战斗（游戏运行中，且有出发记录） */
  restart: boolean;
  /** 有路线板出发记录，游戏运行时可以重开同一遭遇 */
  departure: boolean;
  /** 恢复前自动留下的现场（撤销用）；默认不在清单里显示 */
  preRestore: boolean;
}

export interface ViewState {
  mode: Mode;
  headline: string;
  detail: string;
  gameRunning: boolean;
  lastProtected: { time: string; day: string } | null;
  /** 游戏运行且原生恢复可用时，已注册的游戏内快捷键 */
  hotkeys: { recover: string | null; restart: string | null };
  profile: { found: boolean; name: string };
  snapshots: SnapshotView[];
  /** 宿主希望界面选中的快照（新保存、刚恢复）；nonce 变化才生效 */
  focus: { id: string | null; nonce: number };
  actions: {
    protect: boolean;
    undo: boolean;
    launch: boolean;
    cleanup: boolean;
    remove: boolean;
    /** 有未闭合的实验性暖恢复，需要在旧对话框里处理 */
    warm: boolean;
  };
  /** 恢复事务未闭合等全局阻断；为空表示没有阻断 */
  blocked: string | null;
  settings: {
    autoProtect: boolean;
    autoProtectAvailable: boolean;
    startup: boolean;
    /** 每类只留最近 3 份，其余自动删除 */
    autoCleanup: boolean;
    /** 清单里显示恢复前快照 */
    showUndo: boolean;
    /** 右侧面板的透明度，0 不透明 · 100 只剩磨砂 */
    panelTransparency: number;
    profilePath: string;
  };
  library: { count: number; size: string; preparations: number; scenes: number };
  busy: boolean;
  /** 有原生恢复在进行（包括游戏内快捷键发起的） */
  working: boolean;
}

/** 恢复兵营 / 重开战斗；从哪里开始（结算页、菜单、战斗中、兵营里）由宿主按游戏当时的样子决定 */
export type NativeAction = 'hideout' | 'restart';

export type HostMessage =
  | { type: 'state'; state: ViewState }
  | { type: 'toast'; text: string; tone: Tone }
  /** 恢复面板的进度；text 是给玩家的一两句说明，脚本的原话只进诊断日志 */
  | { type: 'recovery'; phase: 'running' | 'done' | 'failed'; text: string }
  | { type: 'sheet'; sheet: HostSheet };

export type Tone = 'neutral' | 'signal' | 'danger';

export type HostSheet =
  | { kind: 'interrupted' }
  /** 开启自动清理前的确认：现在就要删掉的旧快照 */
  | { kind: 'cleanup'; token: string; removals: number; size: string }
  | { kind: 'written'; time: string }
  | { kind: 'failed'; title: string; text: string };

export type Command =
  | { name: 'ready' }
  | { name: 'protect' }
  | { name: 'restore'; id: string }
  /** 打开确认面板前请宿主先核对；现在就不能做时宿主回一条 failed，面板直接显示原因 */
  | { name: 'native-open'; id: string; action: NativeAction }
  | { name: 'native-start'; id: string; action: NativeAction }
  | { name: 'native-close' }
  | { name: 'undo' }
  | { name: 'remove'; id: string }
  | { name: 'launch' }
  | { name: 'cleanup-confirm'; token: string }
  | { name: 'interrupted-confirm' }
  | { name: 'warm' }
  | { name: 'choose-profile' }
  | { name: 'open-saves' }
  | { name: 'open-backups' }
  | { name: 'auto-protect'; value: boolean }
  | { name: 'startup'; value: boolean }
  | { name: 'auto-cleanup'; value: boolean }
  | { name: 'show-undo'; value: boolean }
  | { name: 'panel-transparency'; value: number }
  | { name: 'window'; action: 'minimize' | 'close' | 'maximize' };
