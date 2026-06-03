using Microsoft.EntityFrameworkCore;
using GeoNex.Models;
using System.IO;

namespace GeoNex.Data;

public class GeoNexContext : DbContext
{
    private readonly string _caminhoArquivoGnx;

    // Construtor: Exige o caminho do arquivo .gnx (Ex: C:\MeusProjetos\Guaratuba.gnx)
    public GeoNexContext(string caminhoArquivoGnx)
    {
        _caminhoArquivoGnx = caminhoArquivoGnx;
    }

    // Estas são as "Tabelas" do nosso banco de dados
    public DbSet<Projeto> Projetos { get; set; }
    public DbSet<Camada> Camadas { get; set; }
    public DbSet<Feicao> Feicoes { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Se a pasta do projeto não existir, a aplicação não pode quebrar.
        // O SQLite criará o arquivo fisicamente neste caminho.
        optionsBuilder.UseSqlite($"Data Source={_caminhoArquivoGnx}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configuração Arquitetônica: Cascata de Deleção
        // Se o engenheiro deletar um Projeto, todas as camadas dele são apagadas do disco.
        modelBuilder.Entity<Camada>()
            .HasOne(c => c.Projeto)
            .WithMany(p => p.Camadas)
            .HasForeignKey(c => c.ProjetoId)
            .OnDelete(DeleteBehavior.Cascade);

        // Se deletar uma Camada, todas as Feições (polígonos e atributos) são apagadas.
        modelBuilder.Entity<Feicao>()
            .HasOne(f => f.Camada)
            .WithMany(c => c.Feicoes)
            .HasForeignKey(f => f.CamadaId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}