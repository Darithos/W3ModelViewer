using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace MdxProbe;

/// <summary>
/// Renders the viewer's unshaded material offscreen over a red backdrop and reads the pixel back —
/// whether its black matte honours the brush's opacity (GEOA fade) and the texture's alpha, or
/// stays an opaque black silhouette.
/// </summary>
public static class WpfBlendProbe
{
    public static int Run()
    {
        int rc = 0;
        var thread = new Thread(() => rc = Body());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return rc;
    }

    private static int Body()
    {
        foreach (bool useNew in new[] { false, true })
            foreach (byte texAlpha in new byte[] { 255, 0 })
                foreach (double opacity in new[] { 1.0, 0.5, 0.0 })
                {
                    var brush = MakeBrush(texAlpha);
                    brush.Opacity = opacity;
                    Material m = useNew
                        ? new MaterialGroup { Children = { new DiffuseMaterial(brush) { Color = Colors.Black, AmbientColor = Colors.Black }, new EmissiveMaterial(brush) } }
                        : new MaterialGroup { Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) } };
                    var px = Render(m);
                    Console.WriteLine($"{(useNew ? "new" : "old")} texAlpha={texAlpha,3} opacity={opacity:0.0} -> R{px.R,3} G{px.G,3} B{px.B,3}");
                }
        return 0;
    }

    /// <summary>A 2x2 cyan texture — the colour of the missile's tint.</summary>
    private static ImageBrush MakeBrush(byte alpha)
    {
        var pixels = new byte[16];
        for (int i = 0; i < 16; i += 4) { pixels[i] = 255; pixels[i + 1] = 230; pixels[i + 2] = 0; pixels[i + 3] = alpha; }
        var bmp = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        return new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
    }

    private static Color Render(Material front)
    {
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(0x66, 0x66, 0x66)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(0x99, 0x99, 0x99), new Vector3D(0, 0, -1)));
        group.Children.Add(Quad(-1, new EmissiveMaterial(Brushes.Red), new DiffuseMaterial(Brushes.Black)));
        group.Children.Add(Quad(0, front, null));

        var visual = new Viewport3DVisual
        {
            Camera = new OrthographicCamera(new Point3D(0, 0, 10), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 4),
            Viewport = new Rect(0, 0, 64, 64),
        };
        visual.Children.Add(new ModelVisual3D { Content = group });
        var rtb = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var buf = new byte[4];
        rtb.CopyPixels(new Int32Rect(32, 32, 1, 1), buf, 4, 0);
        return Color.FromArgb(buf[3], buf[2], buf[1], buf[0]);
    }

    private static GeometryModel3D Quad(double z, Material emissive, Material? under)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { new(-3, -3, z), new(3, -3, z), new(3, 3, z), new(-3, 3, z) },
            TriangleIndices = new System.Windows.Media.Int32Collection { 0, 1, 2, 0, 2, 3 },
            TextureCoordinates = new PointCollection { new(0, 1), new(1, 1), new(1, 0), new(0, 0) },
            Normals = new Vector3DCollection { new(0, 0, 1), new(0, 0, 1), new(0, 0, 1), new(0, 0, 1) },
        };
        Material m = under is null ? emissive : new MaterialGroup { Children = { under, emissive } };
        return new GeometryModel3D(mesh, m);
    }
}
