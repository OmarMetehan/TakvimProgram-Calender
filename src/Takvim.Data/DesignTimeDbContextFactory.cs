using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Takvim.Data;

/// <summary>
/// Yalnızca "dotnet ef" araçları için. Uygulama çalışırken kullanılmaz;
/// göç dosyaları üretilirken DbContext'in nasıl kurulacağını bildirir.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TakvimDbContext>
{
    public TakvimDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TakvimDbContext>()
            .UseSqlite(TakvimPaths.ConnectionString)
            .Options;
        return new TakvimDbContext(options);
    }
}
