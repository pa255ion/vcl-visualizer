% Run one VCL `data` command (Octave), in its work directory: runs code.m and
% writes its numeric and text variables to out.json (matrices as rows), for the
% lesson to use as {name.var}. On failure: err.txt. Runs inside the same
% sandbox as figures (core/src/fig.zig). (Changing the output? Bump vcl.fig_version.)
1; % a script that defines functions

function __vcl_data__()
  try
    __vcl_run__();
  catch err
    msg = err.message;
    for k = 1:numel(err.stack)
      if ~isempty(strfind(err.stack(k).file, 'code.m'))
        msg = sprintf('%s (code line %d)', msg, err.stack(k).line);
        break;
      end
    end
    fid = fopen('err.txt', 'w'); fprintf(fid, '%s', msg); fclose(fid);
  end
end

function __vcl_run__()
  source('code.m');
  names = who();
  s = struct();
  for i = 1:numel(names)
    v = eval(names{i});
    if (isnumeric(v) || islogical(v)) && ndims(v) == 2 && numel(v) <= 20000 && all(isfinite(v(:)))
      if isvector(v) && ~isscalar(v), v = v(:)'; end
      s.(names{i}) = double(v);
    elseif ischar(v) && rows(v) <= 1
      s.(names{i}) = v;
    end
  end
  fid = fopen('out.json', 'w'); fprintf(fid, '%s', jsonencode(s)); fclose(fid);
end
__vcl_data__();
