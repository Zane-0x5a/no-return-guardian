import type { ViewState } from '../types';
import { Button, Slider, Switch } from './Controls';
import { Icon } from './Icons';

interface SettingsProps {
  state: ViewState;
  onAutoProtect: (value: boolean) => void;
  onStartup: (value: boolean) => void;
  onChooseProfile: () => void;
  onOpenSaves: () => void;
  onOpenBackups: () => void;
  onAutoCleanup: (value: boolean) => void;
  onShowUndo: (value: boolean) => void;
  /** 界面里正在预览的值；拖动时先于宿主的状态更新 */
  panelTransparency: number;
  onPanelTransparency: (value: number) => void;
  onCheckUpdates: (value: boolean) => void;
  onUpdate: () => void;
  onOpenRelease: () => void;
}

function shortAccount(name: string) {
  return name.length > 10 ? `${name.slice(0, 4)}…${name.slice(-4)}` : name;
}

export function Settings({
  state, onAutoProtect, onStartup, onChooseProfile, onOpenSaves, onOpenBackups, onAutoCleanup, onShowUndo,
  panelTransparency, onPanelTransparency, onCheckUpdates, onUpdate, onOpenRelease,
}: SettingsProps) {
  const { settings, library, profile, version } = state;
  return (
    <div className="settings">
      <div className="setting">
        <div className="setting-text">
          <span className="setting-name">出发自动保存</span>
          <span className="setting-hint">从路线板出发时，保存出发前的兵营</span>
        </div>
        <Switch
          label="出发自动保存"
          checked={settings.autoProtect}
          disabled={!settings.autoProtectAvailable}
          onChange={onAutoProtect}
        />
      </div>
      <div className="setting">
        <div className="setting-text">
          <span className="setting-name">开机启动</span>
        </div>
        <Switch label="开机启动" checked={settings.startup} onChange={onStartup} />
      </div>
      <div className="setting">
        <div className="setting-text">
          <span className="setting-name">存档</span>
          <span className="setting-hint" title={settings.profilePath}>
            {profile.found ? `账号 ${shortAccount(profile.name)}` : '没有找到游戏存档'}
          </span>
        </div>
        <div className="setting-buttons">
          <Button variant="line" onClick={onChooseProfile}>更换</Button>
          <Button variant="icon" icon={<Icon.folder />} aria-label="打开存档目录" title="存档目录" onClick={onOpenSaves} />
        </div>
      </div>
      <div className="setting">
        <div className="setting-text">
          <span className="setting-name">快照库</span>
          <span className="setting-hint">{library.count} 份 · {library.size}</span>
        </div>
        <div className="setting-buttons">
          <Button variant="icon" icon={<Icon.folder />} aria-label="打开备份目录" title="备份目录" onClick={onOpenBackups} />
        </div>
      </div>
      <div className="setting">
        <div className="setting-text">
          <span className="setting-name">自动清理</span>
          <span className="setting-hint">每类只留最近 3 份，正在用的不删</span>
        </div>
        <Switch
          label="自动清理"
          checked={settings.autoCleanup}
          disabled={!settings.autoCleanup && (!state.actions.cleanup || state.busy)}
          onChange={onAutoCleanup}
        />
      </div>
      <div className="setting">
        <div className="setting-text">
          <span className="setting-name">面板透明度</span>
          <span className="setting-hint">{panelTransparency}% · 越高越透出背后的色场</span>
        </div>
        <Slider label="面板透明度" value={panelTransparency} onChange={onPanelTransparency} />
      </div>
      <div className="setting">
        <div className="setting-text">
          <span className="setting-name">显示恢复前快照</span>
          <span className="setting-hint">{library.scenes ? `${library.scenes} 份 · ` : ''}撤销恢复时用</span>
        </div>
        <Switch label="显示恢复前快照" checked={settings.showUndo} onChange={onShowUndo} />
      </div>
      <div className="setting">
        <div className="setting-text">
          <span className="setting-name">检查更新</span>
          <span className="setting-hint">启动时和每 6 小时查一次 GitHub 上的新版本</span>
        </div>
        <Switch label="检查更新" checked={settings.checkUpdates} onChange={onCheckUpdates} />
      </div>
      <div className="setting">
        <div className="setting-text">
          <span className="setting-name">版本</span>
          <span className="setting-hint">
            {version.available ? `${version.current} · 有新版本 ${version.available}` : version.current}
          </span>
        </div>
        {version.available && (
          <div className="setting-buttons">
            {version.installable && (
              <Button
                variant="line"
                disabled={version.phase === 'downloading' || version.phase === 'installing'}
                onClick={onUpdate}
              >
                更新
              </Button>
            )}
            <Button variant="line" onClick={onOpenRelease}>下载页</Button>
          </div>
        )}
      </div>
    </div>
  );
}
