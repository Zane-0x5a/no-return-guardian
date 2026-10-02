import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { useCallback } from 'react';

type Variant = 'primary' | 'line' | 'danger' | 'danger-solid' | 'ghost' | 'icon';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  icon?: ReactNode;
}

// 把指针在元素里的位置写进 --bx/--by。细线上的高光据此只亮在指针附近。
export function trackPointer(event: React.PointerEvent<HTMLElement>) {
  const rect = event.currentTarget.getBoundingClientRect();
  event.currentTarget.style.setProperty('--bx', `${event.clientX - rect.left}px`);
  event.currentTarget.style.setProperty('--by', `${event.clientY - rect.top}px`);
}

// 细线胶囊按钮。指针停在按钮上时，只有它的细线边框在指针附近被照亮；按下时微微收缩。
export function Button({ variant = 'line', icon, children, className, onPointerMove, ...rest }: ButtonProps) {
  const track = useCallback(
    (event: React.PointerEvent<HTMLButtonElement>) => {
      trackPointer(event);
      onPointerMove?.(event);
    },
    [onPointerMove],
  );
  return (
    <button type="button" className={`btn btn-${variant}${className ? ' ' + className : ''}`} onPointerMove={track} {...rest}>
      {icon}
      {children !== undefined && <span>{children}</span>}
    </button>
  );
}

export function Switch({ checked, disabled, onChange, label }: {
  checked: boolean;
  disabled?: boolean;
  onChange: (value: boolean) => void;
  label: string;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      className={`switch${checked ? ' on' : ''}`}
      onClick={() => onChange(!checked)}
    >
      <span className="switch-knob" />
    </button>
  );
}

// 细线滑轨：一根细线，已选的一段亮一些，一颗小圆钮。用原生 range，键盘和读屏照常可用。
export function Slider({ value, min = 0, max = 100, step = 5, label, onChange }: {
  value: number;
  min?: number;
  max?: number;
  step?: number;
  label: string;
  onChange: (value: number) => void;
}) {
  return (
    <input
      type="range"
      className="slider"
      aria-label={label}
      min={min}
      max={max}
      step={step}
      value={value}
      style={{ '--fill': `${((value - min) / (max - min)) * 100}%` } as React.CSSProperties}
      onChange={(event) => onChange(Number(event.currentTarget.value))}
    />
  );
}

export function Keys({ combo }: { combo: string }) {
  return (
    <span className="keys">
      {combo.split('+').map((key) => (
        <kbd key={key}>{key}</kbd>
      ))}
    </span>
  );
}
