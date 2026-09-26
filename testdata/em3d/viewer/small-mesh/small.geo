// A small real Gmsh mesh for the viewer's .msh reader gate (brief-em3d-28 gate 7): a 1 x 1 x 0.5 box of
// substrate under a box of air, a via cylinder through the substrate, second-order, physical groups
// named the way GmshGeoWriter names them.
SetFactory("OpenCASCADE");
Box(1) = {0, 0, 0, 1, 1, 0.25};
Box(2) = {0, 0, 0.25, 1, 1, 0.25};
Cylinder(3) = {0.5, 0.5, 0, 0, 0, 0.25, 0.08};
BooleanFragments{ Volume{1, 2}; Delete; }{ Volume{3}; Delete; }
via() = Volume In BoundingBox{0.41, 0.41, -0.01, 0.59, 0.59, 0.26};
air() = Volume In BoundingBox{-0.01, -0.01, 0.24, 1.01, 1.01, 0.51};
sub() = Volume In BoundingBox{-0.01, -0.01, -0.01, 1.01, 1.01, 0.26};
sub() -= via();
Physical Volume("substrate") = {sub()};
Physical Volume("air") = {air()};
Physical Volume("via") = {via()};
Physical Surface("outer_pec") = Surface In BoundingBox{-0.01, -0.01, -0.01, 1.01, 1.01, 0.01};
Physical Surface("via_wall") = Surface In BoundingBox{0.41, 0.41, -0.01, 0.59, 0.59, 0.26};
Mesh.MeshSizeMax = 0.12;
Mesh.ElementOrder = 2;
Mesh.MshFileVersion = 2.2;
