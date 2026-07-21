using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteOps.Cloud.Data.Migrations
{
    /// <summary>
    /// Marca <c>RevokedAt</c> e <c>Version</c> de <c>secret_envelopes</c> como tokens de
    /// concorrência otimista (ver <c>AppDbContext.OnModelCreating</c>).
    ///
    /// <para><b>Up/Down vazios de propósito — não é migração esquecida.</b> Token de concorrência
    /// não é DDL: ele não cria coluna nem restrição. O efeito é no SQL que o EF passa a emitir em
    /// tempo de execução, condicionando a UPDATE ao estado que foi lido
    /// (<c>... WHERE "Id" = @id AND "RevokedAt" IS NULL AND "Version" = @lido</c>). Quem perde a
    /// corrida atualiza 0 linhas e recebe <c>DbUpdateConcurrencyException</c> → 409.</para>
    ///
    /// <para>Ela existe para que a mudança fique <b>registrada na cadeia de migrations e no
    /// snapshot do modelo</b>: é uma alteração de segurança (impede que um upsert vivo concorrente
    /// republique o material de uma senha já revogada) e não pode entrar escondida dentro do
    /// snapshot de uma migração futura sobre outro assunto.</para>
    ///
    /// <para>Como não há DDL, também não há nada para reverter: subir e descer esta migração é
    /// inócuo no banco. Nenhum backfill é necessário — as colunas já existem e os valores atuais
    /// são exatamente o estado que o token compara.</para>
    /// </summary>
    public partial class AddSecretEnvelopeConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
