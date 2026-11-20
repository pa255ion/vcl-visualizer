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
s: fig 4.4,-0.4 octave size=8.4 <<END
fs = 800; t = 0:1/fs:2;
x = cos(2*pi*(20*t + 70*t.^2));   % a chirp: 20 Hz rising to 300 Hz
X = abs(fft(x));
f = (0:numel(x)-1) * fs / numel(x);
plot(f(1:end/2), X(1:end/2));
xlabel('frequency (Hz)'); ylabel('|X(f)|'); title('Spectrum of a chirp');
END
say "And Octave computes the spectrum of a chirp: its energy spreads from 20 to 300 hertz."
