# Files you already have: SVG drawings and 3D models (glTF, STL, OBJ) from assets/.
title "Bringing your own files"
svg -4.5,1.5 "assets/ne555.svg" size=4.5 label="ne555.svg"
say "An SVG is drawn crisply at board size."
b: model 0.5,1.4 "assets/bracket.stl" size=2.6 label="bracket.stl"
& spin b 0,30,0
h: model 4.8,1.4 "assets/heatsink.obj" size=2.6 color=#c0c8d0 label="heatsink.obj"
& rot h -20,30,0
p: model 0.5,-2.4 "assets/pyramid.gltf" size=2 label="pyramid.gltf"
& spin p 0,-40,0
say "CAD exports load as 3D models you can turn, highlight or point at."
point b
