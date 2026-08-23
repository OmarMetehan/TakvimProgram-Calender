using Takvim.Server;

// Tarayıcıda tek başına çalıştırma yolu. Masaüstü kabuğu aynı TakvimHost'u
// kendi penceresinde, rastgele bir yerel portla başlatır.
var app = TakvimHost.Build(args);

await TakvimHost.InitializeDatabaseAsync(app);
await app.RunAsync();
