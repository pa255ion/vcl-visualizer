% Render one VCL `fig` (Octave), in its work directory: runs code.m styled to match the board
% and writes out.svg and out.png, meta.json (each axes'
% place in the figure and its data limits) or, on failure, err.txt and a
% figure that shows the error. (Changing the output? Bump vcl.fig_version.)
% Runs inside the sandbox `vcl serve` sets up
% (core/src/fig.zig): no network, nothing writable but the work directory, limited time.
graphics_toolkit('gnuplot');
fg = [0.91 0.93 0.95]; muted = [0.53 0.59 0.67];
palette = [0.259 0.647 0.961; 1 0.655 0.149; 0.4 0.733 0.416; 0.941 0.384 0.573; 0.671 0.278 0.737; 0.149 0.651 0.604];
% gnuplot can't leave the background transparent: paint it the board's colour.
slate = [0.0706 0.0980 0.1412];
set(0, 'defaultfigurevisible', 'off', 'defaultfigurecolor', slate, 'defaultfigureinverthardcopy', 'off', ...
    'defaultaxescolor', slate, 'defaultaxesxcolor', muted, 'defaultaxesycolor', muted, 'defaultaxeszcolor', muted, ...
    'defaultaxescolororder', palette, 'defaultaxesfontsize', 16, 'defaulttextfontsize', 16, 'defaultaxesfontname', 'DejaVu Sans', 'defaulttextfontname', 'DejaVu Sans', 'defaultaxesbox', 'off', ...
    'defaultaxesxgrid', 'on', 'defaultaxesygrid', 'on', 'defaultaxesgridcolor', [0.16 0.21 0.28], ...
    'defaulttextcolor', fg, 'defaultlinelinewidth', 2.6, 'defaultaxeslinewidth', 1);
set(0, 'defaultfigurepaperposition', [0 0 8 5], 'defaultfigurepapersize', [8 5]);
failed = '';
try
  source('code.m');
  if isempty(get(0, 'children'))
    error('the code drew nothing (use plot, surf, imagesc, ...)');
  end
catch err
  failed = err.message;
  for k = 1:numel(err.stack)
    if ~isempty(strfind(err.stack(k).file, 'code.m'))
      failed = sprintf('%s (code line %d)', failed, err.stack(k).line);
      break;
    end
  end
end
if ~isempty(failed)
  close all;
  figure('paperposition', [0 0 8 2.2], 'papersize', [8 2.2], 'color', slate);
  axes('position', [0 0 1 1], 'visible', 'off');
  text(0.03, 0.75, 'figure failed', 'color', [0.94 0.33 0.31], 'fontsize', 20, 'fontweight', 'bold');
  text(0.03, 0.4, failed(1:min(end, 300)), 'color', fg, 'fontsize', 15, 'interpreter', 'none');
  fid = fopen('err.txt', 'w'); fprintf(fid, '%s', failed); fclose(fid);
end
f = gcf();
print(f, 'out.svg', '-dsvg');
% Now the background can go: the board shows through, as with matplotlib.
svg = fileread('out.svg');
fid = fopen('out.svg', 'w'); fprintf(fid, '%s', strrep(svg, '#121924', 'none')); fclose(fid);
% The PNG (for clients that can't draw every SVG): the SVG rasterized, since
% gnuplot's own PNGs lay text out at a fixed size; pngcairo if there's no rsvg.
if system('rsvg-convert -w 1600 -o out.png out.svg') ~= 0
  print(f, 'out.png', '-dpngcairo');
end
if isempty(failed)
  axs = findall(f, 'type', 'axes');
  parts = {};
  for i = numel(axs):-1:1
    a = axs(i);
    p = get(a, 'position');
    xl = get(a, 'xlim'); yl = get(a, 'ylim');
    parts{end+1} = sprintf('{"box":[%g,%g,%g,%g],"x":[%g,%g],"y":[%g,%g],"xlog":%s,"ylog":%s,"name":""}', ...
      p(1), p(2), p(1) + p(3), p(2) + p(4), xl(1), xl(2), yl(1), yl(2), ...
      merge(strcmp(get(a, 'xscale'), 'log'), 'true', 'false'), merge(strcmp(get(a, 'yscale'), 'log'), 'true', 'false'));
  end
  fid = fopen('meta.json', 'w');
  fprintf(fid, '{"size":[8,5],"axes":[%s]}', strjoin(parts, ','));
  fclose(fid);
end
