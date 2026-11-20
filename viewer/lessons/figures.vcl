# Figures from code: matplotlib (Python) and Octave, rendered by the server
# (or ahead of time, for the published copy) and shown on the board.
board 18,10
title "Figures from code"
say "Some pictures are easiest to compute. Here is a damped pendulum, solved numerically."
p: fig -4.2,-0.4 python size=8.4 <<END
from scipy.integrate import solve_ivp
f = lambda t, y: [y[1], -0.25 * y[1] - 9.81 * np.sin(y[0])]
t = np.linspace(0, 20, 2000)
for a in (0.5, 1.5, 2.8):
    s = solve_ivp(f, (0, 20), [a, 0], t_eval=t)
    plt.plot(s.y[0], s.y[1], label=f"start {a} rad")
plt.xlabel("angle θ (rad)")
plt.ylabel("angular velocity ω (rad/s)")
plt.title("Phase portrait")
plt.legend()
END
say "Each curve spirals in toward rest: friction takes a little energy every swing."
r: pt 0,0 on=p color=yellow size=0.18 label="rest"
point 0,0 on=p
say "They all end here, hanging straight down and still."
s: fig 4.4,-0.4 octave size=8.4 <<END
fs = 800; t = 0:1/fs:2;
x = cos(2*pi*(20*t + 70*t.^2));   % a chirp: 20 Hz rising to 300 Hz
X = abs(fft(x));
f = (0:numel(x)-1) * fs / numel(x);
plot(f(1:end/2), X(1:end/2));
xlabel('frequency (Hz)'); ylabel('|X(f)|'); title('Spectrum of a chirp');
END
say "And Octave computes the spectrum of a chirp: its energy spreads from 20 to 300 hertz."
say "Code can also just compute numbers, and the board draws them itself, so they animate."
clear
title "Computed, then drawn"
d: data python <<END
from scipy.integrate import solve_ivp
s = solve_ivp(lambda t, y: [y[1], -0.3 * y[1] - 4 * y[0]], (0, 12), [1, 0], t_eval=np.linspace(0, 12, 160))
curve = np.column_stack([s.t, s.y[0]])
period = round(2 * np.pi / np.sqrt(4 - 0.3**2 / 4), 1)
END
ax: axes 0,-0.6 x=0..12 y=-1..1 size=13,6.5 xlabel="t (s)" ylabel="x (m)"
path {d.curve} on=ax color=cyan dur=3
say "A damped spring, solved by scipy: each swing takes about {d.period} seconds, and every swing is smaller."
