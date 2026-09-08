using System;
using OSGeo.GDAL;
using MaxRev.Gdal.Core;

class Program {
    static void Main() {
        GdalBase.ConfigureAll();
        Gdal.AllRegister();
        string xmlTMS = @""<GDAL_WMS>
  <Service name=\""TMS\"">
    <ServerUrl>https://a.tile.openstreetmap.org/${z}/${x}/${y}.png</ServerUrl>
  </Service>
  <DataWindow>
    <UpperLeftX>-20037508.34</UpperLeftX>
    <UpperLeftY>20037508.34</UpperLeftY>
    <LowerRightX>20037508.34</LowerRightX>
    <LowerRightY>-20037508.34</LowerRightY>
    <TileLevel>19</TileLevel>
    <TileCountX>1</TileCountX>
    <TileCountY>1</TileCountY>
    <YOrigin>top</YOrigin>
  </DataWindow>
  <Projection>EPSG:3857</Projection>
  <BlockSizeX>256</BlockSizeX>
  <BlockSizeY>256</BlockSizeY>
  <BandsCount>3</BandsCount>
</GDAL_WMS>"";
        
        string vsiPath = ""/vsimem/basemap_test.xml"";
        byte[] xmlBytes = System.Text.Encoding.UTF8.GetBytes(xmlTMS);
        Gdal.FileFromMemBuffer(vsiPath, xmlBytes);
        
        try {
            var ds = Gdal.Open(vsiPath, Access.GA_ReadOnly);
            Console.WriteLine(""Opened! RasterCount: "" + ds.RasterCount);
            
            string[] warpArgs = {
                ""-te"", ""-20037508"", ""-20037508"", ""20037508"", ""20037508"",
                ""-ts"", ""500"", ""500"",
                ""-of"", ""MEM""
            };
            using var warpOptions = new GDALWarpAppOptions(warpArgs);
            using var memDs = Gdal.Warp("""", new[] { ds }, warpOptions, null, null);
            Console.WriteLine(""Warped! BandCount: "" + memDs?.RasterCount);
            if (memDs == null) {
                Console.WriteLine(""Warp error: "" + Gdal.GetLastErrorMsg());
            }
        } catch (Exception ex) {
            Console.WriteLine(""Error: "" + ex.Message);
            Console.WriteLine(""GDAL Error: "" + Gdal.GetLastErrorMsg());
        }
    }
}
