-- =====================================================================
--  Banco de dados do Simulador de Troca de Calor - 3 tabelas
--  Compativel com MySQL 8.0+ e MariaDB (XAMPP)
--  Execute UMA vez para criar o banco.
--
--  Ordem de criacao: primeiro as tabelas que NAO dependem de ninguem
--  (material, simulacao), depois a que aponta para elas (corpo).
-- =====================================================================

CREATE DATABASE simulador_calor;
USE simulador_calor;

-- ---------------------------------------------------------------------
-- MATERIAL: catalogo de materiais e suas propriedades fisicas
-- ---------------------------------------------------------------------
CREATE TABLE material (
    id_material       INT          NOT NULL AUTO_INCREMENT,
    nome              VARCHAR(50)  NOT NULL UNIQUE,  -- nao deixa cadastrar o mesmo material duas vezes
    condutividade_k   DOUBLE       NOT NULL,         -- W/(m.K)
    densidade         DOUBLE       NOT NULL,         -- kg/m3
    calor_especifico  DOUBLE       NOT NULL,         -- J/(kg.K)
    PRIMARY KEY (id_material)
);

-- ---------------------------------------------------------------------
-- SIMULACAO: uma linha por execucao (do "Iniciar" ate o fim)
-- ---------------------------------------------------------------------
CREATE TABLE simulacao (
    id_simulacao             INT          NOT NULL AUTO_INCREMENT,
    tamanho_n                INT          NOT NULL,  -- matriz N x N
    dt_s                     DOUBLE       NOT NULL,  -- duracao de cada passo (s)
    temperaturas_aleatorias  BOOLEAN      NOT NULL,
    processamento_paralelo   BOOLEAN      NOT NULL,  -- true = blocos com Parallel.Invoke
    quantidade_blocos        INT          NOT NULL,  -- quantos blocos a matriz foi dividida
    status                   VARCHAR(20)  NOT NULL,  -- EM_ANDAMENTO, EQUILIBRIO ou INTERROMPIDA
    data_inicio              DATETIME     NOT NULL,
    data_fim                 DATETIME     NULL,      -- preenchida no fim (UPDATE)
    tempo_equilibrio_s       DOUBLE       NULL,      -- NULL se nao chegou ao equilibrio
    tempo_processamento_ms   DOUBLE       NULL,      -- tempo real gasto nos calculos (UPDATE no fim)
    PRIMARY KEY (id_simulacao)
);

-- ---------------------------------------------------------------------
-- CORPO: cada cubo em cada iteracao (uma linha por cubo por iteracao)
-- ---------------------------------------------------------------------
CREATE TABLE corpo (
    id_corpo          BIGINT  NOT NULL AUTO_INCREMENT,  -- BIGINT: pode passar de 2 bilhoes de linhas
    id_simulacao      INT     NOT NULL,
    id_material       INT     NOT NULL,
    iteracao          INT     NOT NULL,  -- 0 = estado inicial, 1, 2, 3...
    tempo_simulado_s  DOUBLE  NOT NULL,  -- tempo simulado naquela iteracao
    linha             INT     NOT NULL,  -- posicao na matriz (comeca em 0)
    coluna            INT     NOT NULL,
    lado_m            DOUBLE  NOT NULL,  -- aresta do cubo (m)
    temperatura_k     DOUBLE  NOT NULL,
    massa_kg          DOUBLE  NOT NULL,
    calor_sensivel_j  DOUBLE  NOT NULL,
    PRIMARY KEY (id_corpo),
    FOREIGN KEY (id_simulacao) REFERENCES simulacao (id_simulacao),
    FOREIGN KEY (id_material)  REFERENCES material (id_material),
    -- indice para o SELECT de remontagem (WHERE id_simulacao = ? AND iteracao = ?)
    -- ficar rapido mesmo com milhoes de linhas
    INDEX idx_corpo_simulacao_iteracao (id_simulacao, iteracao)
);

-- ---------------------------------------------------------------------
-- Materiais (mesmos valores de Material.cs)
-- ---------------------------------------------------------------------
INSERT INTO material (nome, condutividade_k, densidade, calor_especifico) VALUES
    ('Cobre',      401,   8960,  385),
    ('Madeira',    0.15,  600,   1700),
    ('Granito',    2.8,   2700,  790),
    ('Concreto',   1.4,   2300,  880),
    ('Chumbo',     35,    11340, 129),
    ('Prata',      429,   10490, 235),
    ('Ouro',       318,   19300, 129),
    ('Tungstenio', 173,   19300, 134),
    ('Aluminio',   237,   2700,  897),
    ('Latao',      109,   8530,  380);

-- =====================================================================
--  CONSULTAS DE EXEMPLO (SELECT) - troque os numeros pelos da sua simulacao
-- =====================================================================

-- 1) Listar as simulacoes
-- SELECT * FROM simulacao;

-- 2) Remontar a matriz na iteracao 10 da simulacao 1
-- SELECT c.linha, c.coluna, m.nome AS material, c.lado_m, c.temperatura_k
-- FROM corpo c
-- JOIN material m ON m.id_material = c.id_material
-- WHERE c.id_simulacao = 1 AND c.iteracao = 10
-- ORDER BY c.linha, c.coluna;

-- 3) Ultima iteracao gravada (estado final)
-- SELECT MAX(iteracao) FROM corpo WHERE id_simulacao = 1;

-- 4) Lista de iteracoes com o tempo simulado (passos entre uma e outra = diferenca / dt_s)
-- SELECT DISTINCT iteracao, tempo_simulado_s FROM corpo
-- WHERE id_simulacao = 1 ORDER BY iteracao;

-- 5) Historico de temperatura de um cubo (linha 0, coluna 0)
-- SELECT iteracao, tempo_simulado_s, temperatura_k FROM corpo
-- WHERE id_simulacao = 1 AND linha = 0 AND coluna = 0 ORDER BY iteracao;

-- 6) Conferir a conservacao de energia: o calor total deve ficar praticamente
--    constante (exceto quando o usuario edita um cubo)
-- SELECT iteracao, SUM(calor_sensivel_j) AS calor_total FROM corpo
-- WHERE id_simulacao = 1 GROUP BY iteracao ORDER BY iteracao;

-- 7) Comparar desempenho: rode uma simulacao com e outra sem paralelismo
-- SELECT id_simulacao, tamanho_n, processamento_paralelo, quantidade_blocos,
--        tempo_processamento_ms FROM simulacao;
