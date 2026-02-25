// Bit organizer inspired by reference images (70mm x 160mm, 48 slots).
$fn = 48;

rows = 8;
cols = 6;

// Slot sizing for 1/4" bits with light clearance.
bitHexAcrossFlats = 6.6;
bitHexRadius = bitHexAcrossFlats / 2;
bitHoleDepth = 18;

// Layout tuned to 70mm width and 160mm height.
plateWidth = 70.0;
plateHeight = 160.0;

holeSpacingX = (plateWidth - 2 * 5.0) / cols;
rowPitch = (plateHeight - 2 * 8.0 - 8.0) / (rows - 1);

shelfDepth = 20.0;
shelfHeight = 8.0;
shelfTilt = -10; // degrees, downward
backOverlap = 1.0;

plateThickness = 4.0;
plateMarginX = 5.0;
plateMarginTop = 8.0;
plateMarginBottom = 8.0;

screwHoleDiameter = 4.0;
screwHeadCounterbore = 7.5;
screwTabHeight = 8.0;
screwHoleInsetX = 6.0;
screwHoleInsetZ = 6.0;
cornerRadius = 3.0;

module hex_hole(depth)
{
  rotate([90, 0, 0])
    cylinder(h = depth, r = bitHexRadius, $fn = 6, center = true);
}

module rounded_rect(width, height, radius)
{
  hull()
  {
    translate([radius, radius, 0]) cylinder(h = 1, r = radius);
    translate([width - radius, radius, 0]) cylinder(h = 1, r = radius);
    translate([width - radius, height - radius, 0]) cylinder(h = 1, r = radius);
    translate([radius, height - radius, 0]) cylinder(h = 1, r = radius);
  }
}

module shelf_row(rowIndex)
{
  zBase = plateMarginBottom + rowIndex * rowPitch;

  translate([0, plateThickness, zBase])
    rotate([shelfTilt, 0, 0])
      translate([0, 0, 0])
        difference()
        {
          translate([0, -backOverlap, 0])
            cube([plateWidth, shelfDepth + backOverlap, shelfHeight]);
          // Add a front chamfer for the stepped shelf look.
          translate([-1, shelfDepth - 6, shelfHeight - 3])
            rotate([45, 0, 0])
              cube([plateWidth + 2, 8, 8], center = false);

          for (colIndex = [0 : cols - 1])
          {
            xPos = plateMarginX + colIndex * holeSpacingX + holeSpacingX / 2;
            yPos = shelfDepth / 2;
            zPos = shelfHeight / 2;

            translate([xPos, yPos, zPos])
              hex_hole(bitHoleDepth);
          }
        }
}

module organizer()
{
  color(partColor)
    difference()
    {
      union()
      {
        linear_extrude(height = plateThickness)
          rounded_rect(plateWidth, plateHeight, cornerRadius);

        for (rowIndex = [0 : rows - 1])
        {
          shelf_row(rowIndex);
        }
      }

      for (xPos = [screwHoleInsetX, plateWidth - screwHoleInsetX])
      {
        for (zPos = [screwHoleInsetZ, plateHeight - screwHoleInsetZ])
        {
          translate([xPos, plateThickness / 2, zPos])
            rotate([90, 0, 0])
              cylinder(h = plateThickness + 1, d = screwHoleDiameter, center = true);
          translate([xPos, plateThickness / 2, zPos])
            rotate([90, 0, 0])
              cylinder(h = plateThickness / 2 + 0.1, d = screwHeadCounterbore, center = true);
        }
      }
    }
}

organizer();
// Color tuned to the cool light-gray plastic in the reference photos.
partColor = [0.66, 0.67, 0.68];
