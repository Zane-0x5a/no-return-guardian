import type { ViewState } from '../types';
import { Button, Keys } from './Controls';
import { Icon } from './Icons';

interface StageProps {
  state: ViewState;
  onProtect: () => void;
  onUndo: () => void;
  onLaunch: () => void;
  onChooseProfile: () => void;
  onWarm: () => void;
  onUpdate: () => void;
}

function updateLabel({ available, installable, phase, progress }: ViewState['version']) {
  if (!installable) return `新版本 ${available}`;
  switch (phase) {
    case 'downloading':
      return `正在下载 ${available} · ${progress}%`;
    case 'installing':
      return `正在安装 ${available}`;
    case 'failed':
      return `没能更新到 ${available}，点这里重试`;
    default:
      return `更新到 ${available}`;
  }
}

// 左侧舞台：只回答三件事——现在守护着吗、最近一次保护在几点、下一步按哪里。
export function Stage({ state, onProtect, onUndo, onLaunch, onChooseProfile, onWarm, onUpdate }: StageProps) {
  const { hotkeys, version } = state;
  const updating = version.phase === 'downloading' || version.phase === 'installing';
  return (
    <section className="stage">
      <header className="wordmark" aria-label="赴死之旅守护器">
        <span>NO RETURN</span>
        <span className="wordmark-rule" />
        <span>GUARDIAN</span>
      </header>
      {version.available && (
        <Button variant="ghost" className="update-note" icon={<Icon.download />} disabled={updating} onClick={onUpdate}>
          {updateLabel(version)}
        </Button>
      )}

      <div className={`status status-${state.mode}`} key={state.headline}>
        <h1 className={`status-headline${state.headline.length > 4 ? ' long' : ''}`}>{state.headline}</h1>
        <p className="status-detail">{state.detail}</p>
        {state.blocked && <p className="status-blocked">{state.blocked}</p>}
      </div>

      {state.lastProtected && (
        <div className="last" aria-label={`最近保护 ${state.lastProtected.day} ${state.lastProtected.time}`}>
          <span className="last-label">最近保护</span>
          <span className="last-time">{state.lastProtected.time}</span>
          <span className="last-day">{state.lastProtected.day}</span>
        </div>
      )}

      <div className="stage-actions">
        {state.profile.found ? (
          <Button variant="primary" icon={<Icon.flag />} disabled={!state.actions.protect} onClick={onProtect}>
            保护当前战备
          </Button>
        ) : (
          <Button variant="primary" icon={<Icon.folder />} onClick={onChooseProfile}>
            选择存档目录
          </Button>
        )}
        <div className="stage-secondary">
          {state.actions.warm && (
            <Button variant="ghost" icon={<Icon.restore />} onClick={onWarm}>
              处理未完成的恢复
            </Button>
          )}
          {state.actions.undo && (
            <Button variant="ghost" icon={<Icon.undo />} onClick={onUndo}>
              撤销最近恢复
            </Button>
          )}
          {state.actions.launch && (
            <Button variant="ghost" icon={<Icon.play />} onClick={onLaunch}>
              启动游戏
            </Button>
          )}
        </div>
      </div>

      {(hotkeys.recover || hotkeys.restart) && (
        <dl className="hotkeys">
          {hotkeys.recover && (
            <div>
              <dt><Keys combo={hotkeys.recover} /></dt>
              <dd>恢复兵营</dd>
            </div>
          )}
          {hotkeys.restart && (
            <div>
              <dt><Keys combo={hotkeys.restart} /></dt>
              <dd>重开战斗</dd>
            </div>
          )}
        </dl>
      )}
    </section>
  );
}
